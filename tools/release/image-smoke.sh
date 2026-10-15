#!/usr/bin/env bash
# Qualify the accepted immutable, non-root browser payload without a database or network.
set -euo pipefail
docker run --rm -i --read-only --network none --tmpfs /tmp/wayfarer:uid=1654,gid=1654,mode=0700 --entrypoint sh "$1" -es <<'SHELL'
test "$(id -u)" = 1654
test ! -w /app
test ! -w /opt/wayfarer-browsers
test ! -w /var/lib/wayfarer
test ! -w /var/cache/wayfarer
test ! -w /var/log/wayfarer
test -z "$(dotnet --list-sdks)"
# Negated commands bypass sh errexit; explicitly fail if a build tool is present.
if command -v node || command -v npm || command -v pwsh; then
  echo "Unexpected build tool in runtime image" >&2
  exit 1
fi
test -x image-helper/wayfarer-image
test -s image-helper/THIRD-PARTY-LICENSES.txt
test -x /usr/bin/prlimit
test -s wwwroot/vite/trip-editor/manifest.json
test -d wwwroot/dist
# Use the native published driver; the runtime has no host Node dependency.
case "$(uname -m)" in x86_64) driver=linux-x64 ;; aarch64) driver=linux-arm64 ;; *) exit 1 ;; esac
exec .playwright/node/$driver/node <<'JAVASCRIPT'
const {chromium}=require("./.playwright/package");
const {spawnSync}=require("node:child_process");
const fs=require("node:fs");
// Exercise the bundled native codec under its real non-root/read-only process controls.
function image(input, cap=52428800) {
  return spawnSync("/usr/bin/prlimit", ["--as=1073741824:1073741824", "--cpu=20:25",
    "--nofile=128:128", "--core=0:0", "--", "/app/image-helper/wayfarer-image", "transform",
    "--width", "600", "--height", "600", "--quality", "85", "--output-format", "auto",
    "--max-input-bytes", "52428800", "--max-output-bytes", String(cap)],
    {input, env:{...process.env, WAYFARER_IMAGE_PARENT_PID:String(process.pid)},
      maxBuffer:52428800, timeout:30000});
}
(async()=>{
  if (process.pid !== 1) throw Error("Image qualification must own container PID 1");
  const browser=await chromium.launch();
  const page=await browser.newPage();
  await page.setContent("<h1>Wayfarer image qualification</h1>");
  await page.pdf({path:"/tmp/wayfarer/probe.pdf"});
  await page.screenshot({path:"/tmp/wayfarer/probe.png"});
  await browser.close();
  if (!fs.statSync("/tmp/wayfarer/probe.pdf").size) throw Error("PDF output missing");
  const source=fs.readFileSync("/tmp/wayfarer/probe.png");
  const transformed=image(source);
  if (transformed.status !== 0 || transformed.stdout.length < 45) {
    console.error("Contained image transform failed", {parentPid:process.pid, exit:transformed.status});
    throw Error("Image transform failed");
  }
  const rejected=image(Buffer.from("malformed image"));
  const capped=image(source, 100);
  if (rejected.status !== 4 || rejected.stdout.length || capped.status !== 70 || capped.stdout.length)
    throw Error("Image rejection/cap failed");
})().catch(()=>process.exit(1));
JAVASCRIPT
SHELL
