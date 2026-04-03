/**
 * Bootstraps global site functionality on DOM ready:
 * initializes Bootstrap tooltips, enables smooth scrolling, and sets up
 * an IntersectionObserver fade-in animation for `.card.hover-lift` elements.
 */
document.addEventListener('DOMContentLoaded', function() {
    // Initialize tooltips
    var tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
    var tooltipList = tooltipTriggerList.map(function (tooltipTriggerEl) {
        return new bootstrap.Tooltip(tooltipTriggerEl);
    });
    
    // Add smooth scroll behavior
    document.documentElement.style.scrollBehavior = 'smooth';
    
    // Animate elements on scroll
    const observerOptions = {
        threshold: 0.1,
        rootMargin: '0px 0px -50px 0px'
    };
    
    const observer = new IntersectionObserver(function(entries) {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                entry.target.style.opacity = '1';
                entry.target.style.transform = 'translateY(0)';
            }
        });
    }, observerOptions);
    
    // Observe stat cards for animation (not the work queue card)
    document.querySelectorAll('.card.hover-lift').forEach(card => {
        card.style.opacity = '0';
        card.style.transform = 'translateY(20px)';
        card.style.transition = 'opacity 0.6s ease, transform 0.6s ease';
        observer.observe(card);
    });
});

// Utility functions

/**
 * Formats a byte count as a human-readable size string.
 * @param {number} bytes - File size in bytes.
 * @returns {string} Formatted string (e.g. "1.23 GB").
 */
function formatFileSize(bytes) {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}

/**
 * Formats a duration in seconds as a compact time string.
 * @param {number} seconds - Duration in seconds.
 * @returns {string} Time string in `H:MM:SS` or `M:SS` format.
 */
function formatDuration(seconds) {
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const secs = Math.floor(seconds % 60);
    
    if (hours > 0) {
        return `${hours}:${minutes.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
    } else {
        return `${minutes}:${secs.toString().padStart(2, '0')}`;
    }
}

/**
 * Formats a bitrate in kbps as a human-readable string, converting to Mbps above 1000 kbps.
 * @param {number} kbps - Bitrate in kilobits per second.
 * @returns {string} Formatted string (e.g. "4.5 Mbps" or "800 kbps").
 */
function formatBitrate(kbps) {
    if (kbps >= 1000) {
        return (kbps / 1000).toFixed(1) + ' Mbps';
    }
    return kbps + ' kbps';
}

/**
 * Replaces a button's content with a loading spinner and disables it.
 * @param {HTMLElement} element - The button element to show the spinner on.
 * @returns {function} A restore function that returns the button to its original state.
 */
function showLoading(element) {
    const originalText = element.innerHTML;
    element.innerHTML = '<span class="spinner-border spinner-border-sm me-2" role="status" aria-hidden="true"></span>Loading...';
    element.disabled = true;
    
    return function() {
        element.innerHTML = originalText;
        element.disabled = false;
    };
}

/**
 * Displays a dismissible Bootstrap toast notification with a Font Awesome icon at the top-right.
 * Uses slide-in/out CSS animations injected into the document head on first call.
 * @param {string} message - Text or HTML content to display.
 * @param {'info'|'success'|'warning'|'danger'} [type='info'] - Bootstrap color variant.
 */
function showToast(message, type = 'info') {
    // Create toast container if it doesn't exist
    let toastContainer = document.getElementById('toast-container');
    if (!toastContainer) {
        toastContainer = document.createElement('div');
        toastContainer.id = 'toast-container';
        toastContainer.className = 'toast-container position-fixed top-0 end-0 p-3';
        toastContainer.style.zIndex = '9999';
        document.body.appendChild(toastContainer);
    }

    // Icon mapping
    const iconMap = {
        'success': 'fa-check-circle',
        'danger': 'fa-exclamation-circle',
        'warning': 'fa-exclamation-triangle',
        'info': 'fa-info-circle'
    };

    // Create toast
    const toastId = 'toast-' + Date.now();
    const toast = document.createElement('div');
    toast.id = toastId;
    toast.className = `toast align-items-center text-bg-${type} border-0`;
    toast.setAttribute('role', 'alert');
    toast.setAttribute('aria-live', 'assertive');
    toast.setAttribute('aria-atomic', 'true');
    toast.style.minWidth = '300px';
    
    const icon = iconMap[type] || 'fa-info-circle';
    
    toast.innerHTML = `
        <div class="d-flex">
            <div class="toast-body d-flex align-items-center">
                <i class="fas ${icon} me-2 fa-lg"></i>
                <span>${message}</span>
            </div>
            <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Close"></button>
        </div>
    `;

    toastContainer.appendChild(toast);

    // Show toast with animation
    const bsToast = new bootstrap.Toast(toast, {
        autohide: true,
        delay: type === 'danger' ? 5000 : 3000
    });
    
    // Add entrance animation
    toast.style.animation = 'slideInRight 0.3s ease-out';
    bsToast.show();

    // Remove toast element after it's hidden
    toast.addEventListener('hidden.bs.toast', function() {
        toast.style.animation = 'slideOutRight 0.3s ease-in';
        setTimeout(() => toast.remove(), 300);
    });
}

// Add CSS animations for toasts
if (!document.getElementById('toast-animations')) {
    const style = document.createElement('style');
    style.id = 'toast-animations';
    style.textContent = `
        @keyframes slideInRight {
            from {
                transform: translateX(100%);
                opacity: 0;
            }
            to {
                transform: translateX(0);
                opacity: 1;
            }
        }
        
        @keyframes slideOutRight {
            from {
                transform: translateX(0);
                opacity: 1;
            }
            to {
                transform: translateX(100%);
                opacity: 0;
            }
        }
    `;
    document.head.appendChild(style);
}
