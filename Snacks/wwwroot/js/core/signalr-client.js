/**
 * SignalR client wrapper.
 *
 * Encapsulates the app's connection to `/transcodingHub` and provides a
 * small, stable API:
 *
 *   - {@link SignalRClient#on}        — subscribe to a hub event.
 *   - {@link SignalRClient#onOpen}    — subscribe to connection/reconnect.
 *   - {@link SignalRClient#onClose}   — subscribe to disconnect/reconnecting.
 *   - {@link SignalRClient#start}     — establish (or re-establish) the connection.
 *
 * The composition root (main.js) owns a single instance and forwards hub
 * events to the owning components.
 *
 * **Resilience notes.** The underlying `HubConnection` already has automatic
 * reconnect configured, but we also:
 *   - Re-call `start()` 5 seconds after a non-intentional close.
 *   - Poll the connection state every 3 seconds and re-fire the appropriate
 *     lifecycle callbacks — iOS Safari can otherwise miss the initial paint.
 */


// ---------------------------------------------------------------------------
// Known hub events
// ---------------------------------------------------------------------------

/**
 * Explicit allow-list of server-emitted events. Registering a handler for an
 * event not in this list via {@link SignalRClient#on} is allowed (the Map
 * simply gets a new entry), but typos in *hub-side* `.SendAsync` calls will
 * fail to bind here and be noticed at start-up.
 */
const KNOWN_EVENTS = Object.freeze([
    'WorkItemAdded',
    'WorkItemUpdated',
    'TranscodingLog',
    'AutoScanCompleted',
    'HistoryCleared',
    'WorkerConnected',
    'WorkerDisconnected',
    'WorkerUpdated',
    'HardwareDetected',
    'ClusterConfigChanged',
    'ClusterNodePaused',
    'NodeSettingsChanged',
    'EncodeHistoryAdded',
    'EncodeHistoryCleared',
]);


// ---------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------

export class SignalRClient {

    constructor() {
        /** @type {signalR.HubConnection|null} */
        this._connection      = null;

        /** True while intentionally tearing down an existing connection so `onclose` can skip the retry. */
        this._intentionalStop = false;

        /** Guards against concurrent `start()` calls. */
        this._initing         = false;

        /** @type {Map<string, Set<Function>>} event name → set of subscriber callbacks */
        this._handlers        = new Map();

        /** Lifecycle callback sets. */
        this._lifecycle       = { open: new Set(), close: new Set() };

        /** Keepalive polling handle (iOS Safari paint fix). */
        this._statusInterval  = null;

        /** Last observed connected state, for edge-triggered lifecycle fan-out. */
        this._lastConnected   = null;
    }

    /**
     * Subscribes `callback` to the named hub event.
     *
     * @param {string}   event    Event name (ideally from {@link KNOWN_EVENTS}).
     * @param {Function} callback Invoked with the event's payload (variadic).
     * @returns {() => void}      Unsubscribe function.
     */
    on(event, callback) {
        if (!this._handlers.has(event)) this._handlers.set(event, new Set());
        this._handlers.get(event).add(callback);

        return () => this._handlers.get(event)?.delete(callback);
    }

    /**
     * Adds a callback fired when the connection is (re)established.
     * @param {() => void} callback
     */
    onOpen(callback) {
        this._lifecycle.open.add(callback);
    }

    /**
     * Adds a callback fired when the connection is lost or begins reconnecting.
     * @param {() => void} callback
     */
    onClose(callback) {
        this._lifecycle.close.add(callback);
    }

    /** Current underlying connection state string, or null if not started. */
    state() {
        return this._connection?.state ?? null;
    }

    /**
     * Starts (or restarts) the hub connection. Safe to call multiple times —
     * an existing connection is torn down first. On failure, schedules a
     * retry after 5 seconds and fires the close-lifecycle callbacks.
     */
    async start() {
        if (this._initing) return;
        this._initing = true;

        try {
            // Tear down any existing connection first so we can fully rewire handlers.
            if (this._connection) {
                this._intentionalStop = true;
                try { await this._connection.stop(); }
                catch { /* already stopped */ }
                this._intentionalStop = false;
            }

            this._connection = new signalR.HubConnectionBuilder()
                .withUrl('/transcodingHub')
                .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
                .build();

            // Bind the allow-listed hub events to the internal fan-out.
            for (const event of KNOWN_EVENTS) {
                this._connection.on(event, (...args) => this._emit(event, ...args));
            }

            // Lifecycle wiring.
            this._connection.onreconnected(() => {
                console.log('SignalR reconnected — resyncing');
                this._lastConnected = true;
                this._lifecycle.open.forEach(cb => cb());
            });

            this._connection.onreconnecting(() => {
                console.log('SignalR reconnecting…');
                this._lastConnected = false;
                this._lifecycle.close.forEach(cb => cb());
            });

            this._connection.onclose(() => {
                if (this._intentionalStop) return;
                console.log('SignalR disconnected — retrying in 5s');
                this._lastConnected = false;
                this._lifecycle.close.forEach(cb => cb());
                setTimeout(() => this.start(), 5000);
            });

            await this._connection.start();
            console.log('SignalR connected');
            this._lastConnected = true;
            this._lifecycle.open.forEach(cb => cb());

        } catch (err) {
            console.error('SignalR connection error:', err);
            this._lifecycle.close.forEach(cb => cb());
            setTimeout(() => this.start(), 5000);

        } finally {
            this._initing = false;
        }

        // Poll-driven safety net: iOS Safari can miss the initial paint
        // without this, since its JS loop is sometimes delayed at wake-up.
        // Edge-triggered — only fire lifecycle callbacks on state transitions,
        // otherwise `loadItems` gets invoked every 3s and reconciles against
        // the server mid-transfer, wiping transient SignalR-only cards.
        if (!this._statusInterval) {
            this._statusInterval = setInterval(() => {
                const connected = this._connection?.state === 'Connected';
                if (connected === this._lastConnected) return;
                this._lastConnected = connected;
                const callbacks = connected ? this._lifecycle.open : this._lifecycle.close;
                callbacks.forEach(cb => cb());
            }, 3000);
        }
    }

    /**
     * Fires every subscriber registered for `event`. Exceptions in individual
     * callbacks are logged and swallowed so one bad subscriber cannot block
     * the others.
     *
     * @param {string}  event
     * @param {...any}  args
     */
    _emit(event, ...args) {
        const set = this._handlers.get(event);
        if (!set) return;

        for (const cb of set) {
            try {
                cb(...args);
            } catch (err) {
                console.error(`Handler for ${event} threw:`, err);
            }
        }
    }
}
