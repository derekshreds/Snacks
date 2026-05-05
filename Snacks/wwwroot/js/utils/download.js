/**
 * Click handler that fetches a URL as a Blob, surfaces a spinner on the
 * triggering button while the request is in flight, and saves the body via a
 * synthesized anchor click. Use for endpoints whose first byte takes seconds
 * — e.g. the master proxying a worker's log archive over the cluster RPC
 * channel — where a plain `<a download>` shows no feedback and feels broken.
 *
 * Honors the server's `Content-Disposition` filename when present and falls
 * back to `fallbackName` otherwise.
 *
 * @param {string}      url
 * @param {HTMLElement} button       Anchor or button that fired the click.
 * @param {string}      [fallbackName]
 */
export async function streamDownload(url, button, fallbackName = 'download.bin') {
    const original = button.innerHTML;
    button.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
    button.setAttribute('aria-busy', 'true');
    button.classList.add('disabled');
    button.style.pointerEvents = 'none';

    try {
        const resp = await fetch(url);
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);

        const disposition = resp.headers.get('content-disposition') || '';
        const match    = /filename\*?=(?:UTF-8'')?"?([^"';]+)"?/i.exec(disposition);
        const filename = (match?.[1] && decodeURIComponent(match[1])) || fallbackName;

        const blob    = await resp.blob();
        const blobUrl = URL.createObjectURL(blob);
        const anchor  = document.createElement('a');
        anchor.href     = blobUrl;
        anchor.download = filename;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        // Hold onto the blob URL long enough for the browser to start the
        // save; revoking too early can race the download dialog on Firefox.
        setTimeout(() => URL.revokeObjectURL(blobUrl), 60_000);
    } catch (err) {
        if (typeof window.showToast === 'function')
            window.showToast(`Download failed: ${err.message}`, 'danger');
        throw err;
    } finally {
        button.innerHTML = original;
        button.removeAttribute('aria-busy');
        button.classList.remove('disabled');
        button.style.pointerEvents = '';
    }
}
