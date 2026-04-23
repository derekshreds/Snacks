/**
 * Auto-scan exclusion-rules panel.
 *
 * Three controls feed into `POST /api/auto-scan/exclusions`:
 *   - filename-pattern chip input,
 *   - resolution chip input,
 *   - a numeric minimum-size-in-GB threshold (blank → no threshold).
 */

import { autoScanApi }                  from '../../api.js';
import { getChipValues, setChipValues } from '../chip-input.js';


// ---------------------------------------------------------------------------
// Read / write
// ---------------------------------------------------------------------------

/**
 * Populates the panel from the persisted exclusion rules. Silent on failure
 * because the endpoint is auth-gated.
 */
async function load() {
    try {
        const rules = await autoScanApi.getExclusions();

        setChipValues('exclusionPatternsChips',    rules.filenamePatterns  || []);
        setChipValues('exclusionResolutionsChips', rules.excludeResolutions || []);
        document.getElementById('exclusionMinSize').value = rules.minSizeGBToSkip ?? '';
    } catch { /* auth-gated */ }
}

/**
 * Persists the current rule set.
 */
async function save() {
    const minSize = document.getElementById('exclusionMinSize').value;

    const body = {
        filenamePatterns:   getChipValues('exclusionPatternsChips'),
        excludeResolutions: getChipValues('exclusionResolutionsChips'),
        minSizeGBToSkip:    minSize === '' ? null : parseFloat(minSize),
    };

    try {
        await autoScanApi.saveExclusions(body);
        showToast('Exclusion rules saved', 'success');
    } catch (e) {
        showToast('Save failed: ' + e.message, 'danger');
    }
}


// ---------------------------------------------------------------------------
// Public entry points
// ---------------------------------------------------------------------------

/** Wires the panel's DOM controls. Safe to call once at startup. */
export function initExclusionPanel() {
    document.getElementById('saveExclusionRules')?.addEventListener('click', save);
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadExclusionPanel = load;
