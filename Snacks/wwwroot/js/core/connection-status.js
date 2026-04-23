/**
 * Nav-bar connection indicator.
 *
 * Thin wrapper around the `#connectionDot` / `#connectionText` elements in
 * the layout. The composition root forwards SignalR lifecycle events to
 * {@link setConnected} and {@link setDisconnected}.
 */


export class ConnectionStatus {

    constructor() {
        this._dot  = document.getElementById('connectionDot');
        this._text = document.getElementById('connectionText');
    }

    /**
     * Paints the indicator in its "connected" state (green dot, "Connected").
     * No-op if the indicator elements are not in the DOM.
     */
    setConnected() {
        if (!this._dot || !this._text) return;

        this._dot.classList.remove('text-danger', 'text-warning');
        this._dot.classList.add('text-success');
        this._text.textContent = 'Connected';
    }

    /**
     * Paints the indicator in its "reconnecting" state (red dot, "Reconnecting...").
     * No-op if the indicator elements are not in the DOM.
     */
    setDisconnected() {
        if (!this._dot || !this._text) return;

        this._dot.classList.remove('text-success', 'text-warning');
        this._dot.classList.add('text-danger');
        this._text.textContent = 'Reconnecting...';
    }
}
