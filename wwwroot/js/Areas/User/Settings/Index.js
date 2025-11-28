document.addEventListener('DOMContentLoaded', () => {
    handleLocationsDeletion();
});

function handleLocationsDeletion() {
    const btn = document.getElementById('deleteAllUserLocations');

    btn.addEventListener('click', (e) => {
        e.preventDefault();
        btn.disabled = true;

        wayfarer.showConfirmationModal({
            title: 'Confirm Deletion',
            message: 'Are you sure you want to delete ALL YOUR LOCATIONS? This cannot be undone!!!',
            confirmText: 'Delete',
            onConfirm: () => {
                fetch(`/api/users/${btn.dataset.userId}/locations`, {
                    method: 'DELETE',
                    credentials: 'include',
                    headers: { 'Accept': 'application/json' },
                })
                    .then(response => {
                        if (response.status === 204) return;
                        if (response.status === 404) return response.json().then(j => Promise.reject(j.message));
                        return Promise.reject(`Unexpected status: ${response.status}`);
                    })
                    .then(() => {
                        // **Hide the correct modal**: deleteConfirmationModal
                        const confirmEl    = document.getElementById('deleteConfirmationModal');
                        const confirmModal = bootstrap.Modal.getInstance(confirmEl);
                        if (confirmModal) confirmModal.hide();

                        wayfarer.showAlert('success', 'All locations deleted successfully.');
                    })
                    .catch(err => {
                        console.error('Delete failed', err);
                        wayfarer.showAlert('danger', typeof err === 'string' ? err : 'Could not delete locations');
                    })
                    .finally(() => {
                        btn.disabled = false;
                    });
            }
        });
    });
}
