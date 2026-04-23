/**
 * Pause/resume control in the nav bar.
 *
 * Owns the `paused` state and mirrors it onto the `#pauseResumeBtn` element.
 * The SignalR hub also pushes pause state from the master to nodes via
 * `ClusterNodePaused`; the composition root forwards those pushes here
 * via {@link PauseControl#setFromRemote} so remote nodes can reflect the
 * master's decision without a server round-trip.
 */

import { queueApi } from '../api.js';


export class PauseControl {

    constructor() {
        this._paused = false;
        this._btn    = document.getElementById('pauseResumeBtn');
        this._icon   = document.getElementById('pauseResumeIcon');
    }

    /** Current pause state (true = paused). */
    get paused() {
        return this._paused;
    }


    // ---- Init ----

    /**
     * Binds the button's click handler and performs the initial fetch.
     * Safe to call once at startup.
     */
    init() {
        this._btn?.addEventListener('click', () => this.toggle());
        this.loadFromServer();
    }


    // ---- Server sync ----

    /**
     * Fetches the authoritative pause state from the server and updates
     * the local mirror.
     */
    async loadFromServer() {
        try {
            const data = await queueApi.getPaused();
            this._set(!!data.paused);
        } catch (err) {
            console.error('Error loading pause state:', err);
        }
    }

    /**
     * Flips the paused state server-side, updates the local mirror with
     * the authoritative response, and toasts a status line.
     */
    async toggle() {
        try {
            const data = await queueApi.setPaused(!this._paused);
            this._set(!!data.paused);

            const msg = this._paused
                ? 'Queue paused — current encode will finish'
                : 'Queue resumed';
            showToast(msg, 'info');
        } catch (err) {
            console.error('Error toggling pause:', err);
            showToast('Error toggling pause: ' + err.message, 'danger');
        }
    }

    /**
     * Updates the local mirror from a hub push (master → node) without a
     * server round-trip.
     *
     * @param {boolean} paused
     */
    setFromRemote(paused) {
        this._set(!!paused);
    }


    // ---- Internal ----

    /**
     * Writes the new state and repaints the button.
     *
     * @param {boolean} paused
     */
    _set(paused) {
        this._paused = paused;
        if (!this._btn || !this._icon) return;

        if (paused) {
            this._icon.className = 'fas fa-play';
            this._btn.className  = 'btn btn-outline-warning btn-sm me-2';
            this._btn.title      = 'Resume Queue';
        } else {
            this._icon.className = 'fas fa-pause';
            this._btn.className  = 'btn btn-outline-secondary btn-sm me-2';
            this._btn.title      = 'Pause Queue';
        }
    }
}
