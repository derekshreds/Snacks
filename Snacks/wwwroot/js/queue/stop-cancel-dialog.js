/**
 * Stop / cancel dialog.
 *
 * Shown from a work-item's trash-can button. Two actions:
 *   - **Stop**   — halt the current encode but leave the file eligible for
 *                  a future scan (re-queued on next auto-scan).
 *   - **Cancel** — halt the current encode and mark the file as
 *                  "do not process again."
 *
 * Each invocation clones the buttons so callbacks from prior opens are
 * dropped — no listener leakage across reopens.
 */

import { openModal, closeModal } from '../utils/modal-controller.js';


// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const MODAL_ID = 'stopCancelModal';


// ---------------------------------------------------------------------------
// Dialog
// ---------------------------------------------------------------------------

export class StopCancelDialog {

    /**
     * @param {(id: string) => void | Promise<void>} onStop
     * @param {(id: string) => void | Promise<void>} onCancel
     */
    constructor(onStop, onCancel) {
        this._onStop   = onStop;
        this._onCancel = onCancel;
    }

    /**
     * Opens the dialog for `workItemId` and rewires the stop/cancel buttons.
     *
     * @param {string} workItemId
     */
    show(workItemId) {

        // Clone both action buttons so each invocation gets a clean listener.
        const stopBtn   = document.getElementById('stopCancelStop');
        const cancelBtn = document.getElementById('stopCancelCancel');

        const newStop   = stopBtn.cloneNode(true);
        const newCancel = cancelBtn.cloneNode(true);
        stopBtn.replaceWith(newStop);
        cancelBtn.replaceWith(newCancel);

        newStop.addEventListener('click', async () => {
            closeModal(MODAL_ID);
            await this._onStop(workItemId);
        });

        newCancel.addEventListener('click', async () => {
            closeModal(MODAL_ID);
            await this._onCancel(workItemId);
        });

        openModal(MODAL_ID);
    }
}
