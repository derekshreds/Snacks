/**
 * Formats the safe capability list returned by the Luna connector status API.
 * Kept pure so pluralization stays covered by the frontend test suite.
 *
 * @param {unknown} capabilities
 * @returns {string}
 */
export function formatLunaCapabilitySummary(capabilities) {
    const count = Array.isArray(capabilities) ? capabilities.length : 0;
    return `${count} ${count === 1 ? 'capability' : 'capabilities'} enabled`;
}
