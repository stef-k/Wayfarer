"""Retained browser subprocess ownership using Linux birth tokens or Windows jobs.

Linux children get separate sessions and pidfds. Windows children start suspended,
are assigned to a retained job, then resume; the job owns even early descendants.
Records are diagnostic evidence, never authority to reopen and terminate a PID.
"""

import ctypes
from ctypes import wintypes
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import time


def port_free(port):
    """Require exclusive loopback binding instead of treating a foreign host as ready."""
    try:
        with socket.socket() as listener:
            if os.name != 'nt':
                listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(('127.0.0.1', port))
        return True
    except OSError:
        return False


def owns_listener(child, port):
    """Bind HTTP readiness to the launched process, not merely an occupied endpoint."""
    if child.process.poll() is not None:
        return False
    child.inspect()
    if os.name == 'nt':
        library = ctypes.WinDLL('iphlpapi', use_last_error=True)
        query = library.GetExtendedTcpTable
        query.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD), wintypes.BOOL,
                          wintypes.ULONG, ctypes.c_int, wintypes.ULONG]
        size = wintypes.DWORD(0)
        query(None, ctypes.byref(size), False, 2, 3, 0)
        buffer = ctypes.create_string_buffer(size.value)
        if query(buffer, ctypes.byref(size), False, 2, 3, 0):
            raise RuntimeError('Cannot inspect endpoint ownership')
        rows = ctypes.cast(buffer, ctypes.POINTER(wintypes.DWORD))
        return any(rows[1 + index * 6] == 2 and
                   socket.ntohs(rows[3 + index * 6] & 0xffff) == port and
                   rows[6 + index * 6] == child.process.pid for index in range(rows[0]))
    sockets = set()
    for entry in Path(f'/proc/{child.process.pid}/fd').iterdir():
        try:
            sockets.add(os.readlink(entry))
        except FileNotFoundError:
            continue
    for table in ('tcp', 'tcp6'):
        for row in Path(f'/proc/net/{table}').read_text().splitlines()[1:]:
            fields = row.split()
            if int(fields[1].split(':')[1], 16) == port and fields[3] == '0A':
                if f'socket:[{fields[9]}]' in sockets:
                    return True
    return False


def identity(pid, handle=None):
    """Return native creation identity without estimating a wall-clock boot epoch."""
    if sys.platform == 'linux':
        fields = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
        return {'pid': pid, 'boot': Path('/proc/sys/kernel/random/boot_id').read_text().strip(),
                'start': fields[19]}
    if os.name == 'nt':
        opened = handle is None
        handle = handle or _win('OpenProcess', 0x1000, False, pid)
        try:
            times = [wintypes.FILETIME() for _ in range(4)]
            _win('GetProcessTimes', handle, *(ctypes.byref(item) for item in times))
            return {'pid': pid, 'start': (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime}
        finally:
            if opened:
                _win('CloseHandle', handle)
    raise RuntimeError('Browser ownership requires Linux/WSL or Windows')


def _win(name, *args):
    """Call the narrow, checked Win32 handle API with pointer-width-safe declarations."""
    signatures = {
        'OpenProcess': (wintypes.HANDLE, [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]),
        'GetProcessTimes': (wintypes.BOOL, [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4),
        'CloseHandle': (wintypes.BOOL, [wintypes.HANDLE]),
        'CreateJobObjectW': (wintypes.HANDLE, [ctypes.c_void_p, wintypes.LPCWSTR]),
        'AssignProcessToJobObject': (wintypes.BOOL, [wintypes.HANDLE, wintypes.HANDLE]),
        'TerminateJobObject': (wintypes.BOOL, [wintypes.HANDLE, wintypes.UINT]),
        'QueryInformationJobObject': (wintypes.BOOL, [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                                     wintypes.DWORD, ctypes.c_void_p]),
        'CreateToolhelp32Snapshot': (wintypes.HANDLE, [wintypes.DWORD, wintypes.DWORD]),
        'Thread32First': (wintypes.BOOL, [wintypes.HANDLE, ctypes.c_void_p]),
        'Thread32Next': (wintypes.BOOL, [wintypes.HANDLE, ctypes.c_void_p]),
        'OpenThread': (wintypes.HANDLE, [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]),
        'GetProcessIdOfThread': (wintypes.DWORD, [wintypes.HANDLE]),
        'ResumeThread': (wintypes.DWORD, [wintypes.HANDLE]),
    }
    library = ctypes.WinDLL('kernel32', use_last_error=True)
    function = getattr(library, name)
    function.restype, function.argtypes = signatures[name]
    result = function(*args)
    if not result and name not in ('Thread32Next', 'ResumeThread'):
        raise ctypes.WinError(ctypes.get_last_error())
    if name == 'CreateToolhelp32Snapshot' and result == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    return result


def _resume(process):
    """Resume the sole suspended initial thread only after verifying its process owner."""
    class ThreadEntry(ctypes.Structure):
        _fields_ = [('size', wintypes.DWORD), ('usage', wintypes.DWORD),
                    ('tid', wintypes.DWORD), ('pid', wintypes.DWORD),
                    ('base', wintypes.LONG), ('delta', wintypes.LONG), ('flags', wintypes.DWORD)]
    entry = ThreadEntry()
    entry.size = ctypes.sizeof(entry)
    snapshot = _win('CreateToolhelp32Snapshot', 4, 0)
    try:
        more = _win('Thread32First', snapshot, ctypes.byref(entry))
        while more:
            if entry.pid == process.pid:
                thread = _win('OpenThread', 0x0802, False, entry.tid)
                try:
                    if _win('GetProcessIdOfThread', thread) != process.pid:
                        raise RuntimeError('Suspended thread ownership changed')
                    if _win('ResumeThread', thread) == 0xffffffff:
                        raise ctypes.WinError(ctypes.get_last_error())
                    return
                finally:
                    _win('CloseHandle', thread)
            more = _win('Thread32Next', snapshot, ctypes.byref(entry))
        raise RuntimeError('Suspended child thread not found')
    finally:
        _win('CloseHandle', snapshot)


class Child:
    """Owns one launched process and all writers in its native session/job."""

    def __init__(self, command, *, cwd, env, log):
        self.command = list(map(str, command))
        self.log = log.open('xb')
        self.members = {}
        self.job = None
        self.closed = False
        options = {'start_new_session': True} if os.name != 'nt' else {'creationflags': 4}
        self.process = subprocess.Popen(self.command, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                        stdout=self.log, stderr=subprocess.STDOUT, **options)
        try:
            self.record = identity(self.process.pid, getattr(self.process, '_handle', None))
            if os.name == 'nt':
                self.job = _win('CreateJobObjectW', None, None)
                _win('AssignProcessToJobObject', self.job, int(self.process._handle))
                _resume(self.process)
            else:
                self._retain(self.process.pid)
        except BaseException:
            # The retained newly created reference is safe even when setup cannot complete.
            self.process.kill()
            self.process.wait(timeout=10)
            self.log.close()
            if self.job:
                _win('CloseHandle', self.job)
            raise

    def _retain(self, pid):
        """Pin an inspected session member before admitting it to stop authority."""
        before = identity(pid)
        descriptor = os.pidfd_open(pid) if hasattr(os, 'pidfd_open') else None
        if before != identity(pid):
            if descriptor is not None:
                os.close(descriptor)
            raise RuntimeError('Child identity changed during inspection')
        self.members[pid] = before, descriptor

    def inspect(self):
        """Include reparented descendants by session, and refuse changed retained identities."""
        if os.name == 'nt':
            if identity(self.process.pid, int(self.process._handle)) != self.record:
                raise RuntimeError('Child creation identity changed')
            return
        for path in Path('/proc').iterdir():
            if not path.name.isdigit():
                continue
            try:
                fields = (path / 'stat').read_text().rsplit(')', 1)[1].split()
                if int(fields[3]) == self.process.pid and fields[0] != 'Z':
                    pid = int(path.name)
                    if pid not in self.members:
                        self._retain(pid)
                    elif identity(pid) != self.members[pid][0]:
                        raise RuntimeError('Reused child PID refused')
            except (FileNotFoundError, ProcessLookupError):
                continue
        if self.process.poll() is None and identity(self.process.pid) != self.record:
            raise RuntimeError('Forged/reused child identity refused')

    def _live(self):
        """Find remaining writers without treating zombies or reaped parents as active."""
        if os.name == 'nt':
            class Accounting(ctypes.Structure):
                _fields_ = [('times', ctypes.c_longlong * 4), ('faults', wintypes.DWORD),
                            ('total', wintypes.DWORD), ('active', wintypes.DWORD), ('ended', wintypes.DWORD)]
            value = Accounting()
            _win('QueryInformationJobObject', self.job, 1, ctypes.byref(value), ctypes.sizeof(value), None)
            return bool(value.active)
        active = []
        for pid, (record, descriptor) in self.members.items():
            try:
                fields = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
                if fields[0] != 'Z':
                    if identity(pid) != record:
                        raise RuntimeError('Reused descendant PID refused')
                    active.append((pid, descriptor))
            except FileNotFoundError:
                continue
        return active

    def stop(self):
        """Stop only retained references, verify every writer exited, reap and close logs."""
        if self.closed:
            return
        self.inspect()
        if os.name == 'nt':
            if self._live():
                _win('TerminateJobObject', self.job, 1)
        else:
            for stop_signal in (signal.SIGTERM, signal.SIGKILL):
                self.inspect()
                for pid, descriptor in self._live():
                    try:
                        if descriptor is not None:
                            signal.pidfd_send_signal(descriptor, stop_signal)
                        elif pid == self.process.pid and self.process.poll() is None:
                            self.process.send_signal(stop_signal)
                        else:
                            raise RuntimeError('Cannot pin descendant signaling; preserving residue')
                    except ProcessLookupError:
                        pass  # A pinned member may exit between inspection and signaling.
                deadline = time.monotonic() + 5
                while self._live() and time.monotonic() < deadline:
                    self.inspect()
                    time.sleep(0.05)
                if not self._live():
                    break
        self.process.wait(timeout=10)
        if self._live():
            raise RuntimeError('Owned descendants remain; preserving residue')
        self.log.close()
        for _, descriptor in self.members.values():
            if descriptor is not None:
                os.close(descriptor)
        if self.job:
            _win('CloseHandle', self.job)
        self.closed = True

    def wait(self, timeout, watch=None):
        """Observe early host death while a bounded build/helper/browser command runs."""
        deadline = time.monotonic() + timeout
        while self.process.poll() is None:
            self.inspect()
            if watch is not None and watch.process.poll() is not None:
                raise RuntimeError('Application host terminated during child execution')
            if time.monotonic() >= deadline:
                raise TimeoutError('Owned command exceeded its deadline')
            time.sleep(0.05)
        return self.process.returncode
