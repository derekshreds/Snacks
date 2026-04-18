/**
 * Classic-globals bootstrap and shared formatting utilities.
 *
 * This file runs outside the ES-module graph (it is loaded with a plain
 * `<script>` tag before `main.js`) and defines a handful of functions that
 * inline event handlers and legacy razor partials still reference as
 * unqualified globals: `formatFileSize`, `formatDuration`, `formatBitrate`,
 * `showLoading`, and `showToast`.
 *
 * On `DOMContentLoaded` it also:
 *   - initializes Bootstrap tooltips,
 *   - enables CSS smooth-scrolling,
 *   - sets up an IntersectionObserver fade-in animation for
 *     `.card.hover-lift` elements.
 */


// ---------------------------------------------------------------------------
// Document-ready bootstrap
// ---------------------------------------------------------------------------

document.addEventListener('DOMContentLoaded', function () {

    // Initialize Bootstrap tooltips on every opted-in element.
    const tooltipTriggerList = Array.from(
        document.querySelectorAll('[data-bs-toggle="tooltip"]'),
    );
    tooltipTriggerList.map((tooltipTriggerEl) =>
        new bootstrap.Tooltip(tooltipTriggerEl),
    );

    // Smooth in-page anchor scrolling.
    document.documentElement.style.scrollBehavior = 'smooth';

    // Animate `.card.hover-lift` into view on scroll.
    const observerOptions = {
        threshold:  0.1,
        rootMargin: '0px 0px -50px 0px',
    };

    const observer = new IntersectionObserver(function (entries) {
        entries.forEach((entry) => {
            if (!entry.isIntersecting) return;
            entry.target.style.opacity   = '1';
            entry.target.style.transform = 'translateY(0)';
        });
    }, observerOptions);

    document.querySelectorAll('.card.hover-lift').forEach((card) => {
        card.style.opacity    = '0';
        card.style.transform  = 'translateY(20px)';
        card.style.transition = 'opacity 0.6s ease, transform 0.6s ease';
        observer.observe(card);
    });
});


// ---------------------------------------------------------------------------
// Formatting utilities (exposed as globals for legacy call-sites)
// ---------------------------------------------------------------------------

/**
 * Formats a byte count as a human-readable size string.
 *
 * @param {number} bytes - File size in bytes.
 * @returns {string}     Formatted string (e.g. "1.23 GB").
 */
function formatFileSize(bytes) {
    if (!bytes || bytes <= 0) return '0 Bytes';

    const k     = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB'];
    const i     = Math.min(Math.floor(Math.log(bytes) / Math.log(k)), sizes.length - 1);

    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}

/**
 * Formats a duration in seconds as a compact time string.
 *
 * @param {number} seconds - Duration in seconds.
 * @returns {string}       Time string in `H:MM:SS` or `M:SS` format.
 */
function formatDuration(seconds) {
    const hours   = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const secs    = Math.floor(seconds % 60);

    if (hours > 0) {
        return `${hours}:${minutes.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
    }
    return `${minutes}:${secs.toString().padStart(2, '0')}`;
}

/**
 * Formats a bitrate in kbps as a human-readable string, converting to
 * Mbps above 1000 kbps.
 *
 * @param {number} kbps - Bitrate in kilobits per second.
 * @returns {string}    Formatted string (e.g. "4.5 Mbps" or "800 kbps").
 */
function formatBitrate(kbps) {
    if (kbps >= 1000) {
        return (kbps / 1000).toFixed(1) + ' Mbps';
    }
    return kbps + ' kbps';
}


// ---------------------------------------------------------------------------
// Loading-state helper
// ---------------------------------------------------------------------------

/**
 * Replaces a button's content with a loading spinner and disables it.
 *
 * @param {HTMLElement} element - The button element to show the spinner on.
 * @returns {function}          A restore function that returns the button
 *                              to its original state.
 */
function showLoading(element) {
    const originalText = element.innerHTML;

    element.innerHTML = '<span class="spinner-border spinner-border-sm me-2" role="status" aria-hidden="true"></span>Loading...';
    element.disabled  = true;

    return function () {
        element.innerHTML = originalText;
        element.disabled  = false;
    };
}


// ---------------------------------------------------------------------------
// Toast notifications
// ---------------------------------------------------------------------------

/**
 * Displays a dismissible Bootstrap toast notification with a Font Awesome
 * icon at the top-right of the viewport.
 *
 * The toast container and its slide-in/out CSS animations are injected
 * lazily the first time this function is invoked.
 *
 * @param {string} message                               Text or HTML to display.
 * @param {'info'|'success'|'warning'|'danger'} [type='info'] Bootstrap color variant.
 */
function showToast(message, type = 'info') {

    // Create the toast container on first use.
    let toastContainer = document.getElementById('toast-container');
    if (!toastContainer) {
        toastContainer           = document.createElement('div');
        toastContainer.id        = 'toast-container';
        toastContainer.className = 'toast-container position-fixed top-0 end-0 p-3';
        toastContainer.style.zIndex = '9999';
        document.body.appendChild(toastContainer);
    }

    const iconMap = {
        success: 'fa-check-circle',
        danger:  'fa-exclamation-circle',
        warning: 'fa-exclamation-triangle',
        info:    'fa-info-circle',
    };

    const toastId = 'toast-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);

    const toast = document.createElement('div');
    toast.id        = toastId;
    toast.className = `toast align-items-center text-bg-${type} border-0`;
    toast.setAttribute('role',       'alert');
    toast.setAttribute('aria-live',  'assertive');
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

    const bsToast = new bootstrap.Toast(toast, {
        autohide: true,
        delay:    type === 'danger' ? 5000 : 3000,
    });

    // Slide-in animation.
    toast.style.animation = 'slideInRight 0.3s ease-out';
    bsToast.show();

    // Animate out and remove from the DOM after Bootstrap hides the toast.
    toast.addEventListener('hidden.bs.toast', function () {
        toast.style.animation = 'slideOutRight 0.3s ease-in';
        setTimeout(() => toast.remove(), 300);
    });
}


// ---------------------------------------------------------------------------
// One-time CSS injection for the toast slide animations
// ---------------------------------------------------------------------------

if (!document.getElementById('toast-animations')) {
    const style = document.createElement('style');
    style.id    = 'toast-animations';
    style.textContent = `
        @keyframes slideInRight {
            from {
                transform: translateX(100%);
                opacity:   0;
            }
            to {
                transform: translateX(0);
                opacity:   1;
            }
        }

        @keyframes slideOutRight {
            from {
                transform: translateX(0);
                opacity:   1;
            }
            to {
                transform: translateX(100%);
                opacity:   0;
            }
        }
    `;
    document.head.appendChild(style);
}
