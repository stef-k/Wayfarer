// Make sure the function is globally available
function hideAlert() {
    let alertBox = document.getElementById("alertBox");
    if (alertBox) {
        alertBox.classList.remove("show");
        setTimeout(() => {
            alertBox.style.display = "none";
        }, 150);
    }
}

function showAlert(alertType, alertMessage) {
    let alertBox = document.getElementById("alertBox");
    let alertMessageSpan = document.getElementById("alertMessage");
    
    if (!alertBox || !alertMessageSpan) {
        console.error("❌ Alert elements NOT found in the DOM!");
        return;
    }
    alertMessageSpan.textContent = alertMessage;

    // Reset class and show alert
    alertBox.className = `alert alert-${alertType} alert-dismissible show`;
    alertBox.style.display = "block";
    alertBox.style.opacity = "1";
}


function showConfirmationModal(options) {
    let modal = document.getElementById('deleteConfirmationModal');
    if (!modal) {
        console.error('Modal element #deleteConfirmationModal not found in the DOM.');
        return;
    }

    const modalInstance = new bootstrap.Modal(modal);

    // Set up the modal content dynamically
    const modalTitle = modal.querySelector('.modal-title');
    const modalBody = modal.querySelector('.modal-body');
    const confirmButton = modal.querySelector('.btn-danger'); // Assuming the confirm button has a 'btn-danger' class

    // Set the default values if the options are not provided
    modalTitle.textContent = options.title || 'Confirm Action';
    modalBody.textContent = options.message || 'Are you sure you want to proceed?';
    confirmButton.textContent = options.confirmText || 'Confirm';

    // Handle the confirmation action
    confirmButton.onclick = function () {
        if (options.onConfirm && typeof options.onConfirm === 'function') {
            options.onConfirm(); // Execute the provided callback
        }
        modalInstance.hide(); // Close the modal after the action
    };

    // Show the modal
    modalInstance.show();
}

// Now the rest of the code inside the DOMContentLoaded event listener
document.addEventListener('DOMContentLoaded', () => {
    const toggleButton = document.getElementById('themeToggle');
    const themeIcon = document.getElementById('themeIcon');
    const body = document.body;
    const navbar = document.getElementById('mainNavbar');

    // Function to set theme
    const setTheme = (theme) => {
        if (theme === 'dark') {
            body.setAttribute('data-bs-theme', 'dark');
            themeIcon.classList.remove('bi-sun');
            themeIcon.classList.add('bi-moon');
            navbar.classList.add('navbar-dark', 'bg-dark');
            navbar.classList.remove('navbar-light', 'bg-white');
        } else {
            body.setAttribute('data-bs-theme', 'light');
            themeIcon.classList.remove('bi-moon');
            themeIcon.classList.add('bi-sun');
            navbar.classList.add('navbar-light', 'bg-white');
            navbar.classList.remove('navbar-dark', 'bg-dark');
        }

        // Persist theme in localStorage
        localStorage.setItem('theme', theme);

        // Update text color for navbar items
        updateNavbarTextColor(theme);
    };

    // Function to update navbar link text color based on theme
    const updateNavbarTextColor = (theme) => {
        const links = document.querySelectorAll('.theme-toggle');
        links.forEach(link => {
            if (theme === 'dark') {
                link.classList.remove('text-dark');
                link.classList.add('text-light');
            } else {
                link.classList.remove('text-light');
                link.classList.add('text-dark');
            }
        });
    };

    // Initialize theme based on localStorage
    const storedTheme = localStorage.getItem('theme') || 'light';
    setTheme(storedTheme);

    // Toggle theme on button click
    toggleButton.addEventListener('click', () => {
        const currentTheme = body.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
        setTheme(currentTheme);
    });
});
