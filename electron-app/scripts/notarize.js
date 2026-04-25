/**
 * electron-builder afterSign hook.
 *
 * Submits the signed .app bundle to Apple's notary service via notarytool.
 * Skips silently when not on macOS or when the required env vars aren't set,
 * so unsigned dev builds (./build-mac.sh without .env.mac.local) still work.
 *
 * Required env vars (all three or none):
 *   APPLE_ID                       Apple ID email used for the Developer Program
 *   APPLE_APP_SPECIFIC_PASSWORD    App-specific password from appleid.apple.com
 *   APPLE_TEAM_ID                  10-char Team ID from developer.apple.com/account
 *
 * Stapling is handled automatically by electron-builder once notarize() returns.
 */

const { notarize } = require("@electron/notarize");

exports.default = async function (context) {
    if (context.electronPlatformName !== "darwin") return;

    const { APPLE_ID, APPLE_APP_SPECIFIC_PASSWORD, APPLE_TEAM_ID } = process.env;
    if (!APPLE_ID || !APPLE_APP_SPECIFIC_PASSWORD || !APPLE_TEAM_ID) {
        console.log(
            "[notarize] Skipping — APPLE_ID / APPLE_APP_SPECIFIC_PASSWORD / APPLE_TEAM_ID not all set. " +
            "Build will be unsigned-or-signed-but-not-notarized."
        );
        return;
    }

    const appName = context.packager.appInfo.productFilename;
    const appPath = `${context.appOutDir}/${appName}.app`;

    console.log(`[notarize] Submitting ${appPath} to Apple notary service...`);
    await notarize({
        tool:            "notarytool",
        appPath,
        appleId:         APPLE_ID,
        appleIdPassword: APPLE_APP_SPECIFIC_PASSWORD,
        teamId:          APPLE_TEAM_ID,
    });
    console.log("[notarize] Notarization complete; electron-builder will staple the ticket.");
};
