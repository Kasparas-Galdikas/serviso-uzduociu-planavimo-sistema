(() => {
    const TRACE_KEY = "schedule:traceId";
    const BUFFER_KEY = "schedule:traceBuffer";
    const TRACE_COOKIE = "schedule_trace";

    const safeSet = (key, val) => {
        try { sessionStorage.setItem(key, val); } catch { /* ignore */ }
    };
    const safeGet = (key) => {
        try { return sessionStorage.getItem(key); } catch { return null; }
    };
    const safeRemove = (key) => {
        try { sessionStorage.removeItem(key); } catch { /* ignore */ }
    };
    const setCookie = (name, value) => {
        try {
            document.cookie = `${name}=${encodeURIComponent(value)}; path=/`;
        } catch { /* ignore */ }
    };
    const readCookie = (name) => {
        try {
            const match = document.cookie.split(";").map(c => c.trim()).find(c => c.startsWith(name + "="));
            if (!match) return null;
            return decodeURIComponent(match.substring(name.length + 1));
        } catch { return null; }
    };

    const genId = () => {
        if (crypto?.randomUUID) return crypto.randomUUID().replace(/-/g, "");
        return Math.random().toString(16).slice(2) + Date.now().toString(16);
    };

    const state = {
        traceId: null,
        spanSeed: 0,
        buffer: [],
        maxEntries: 200
    };

    const loadBuffer = () => {
        const raw = safeGet(BUFFER_KEY);
        if (!raw) return;
        try {
            const parsed = JSON.parse(raw);
            if (Array.isArray(parsed)) {
                state.buffer = parsed;
            }
        } catch { /* ignore */ }
    };
    loadBuffer();

    const push = (entry) => {
        state.buffer.push(entry);
        if (state.buffer.length > state.maxEntries) {
            state.buffer.shift();
        }
        safeSet(BUFFER_KEY, JSON.stringify(state.buffer));
        const line = `[${entry.level}] ${entry.event} trace=${entry.traceId} span=${entry.spanId}`;
        // Structured console output for quick glance.
        (console[entry.level] || console.log)(line, entry.data || {});
    };

    const setTraceId = (val) => {
        state.traceId = val;
        if (val) {
            safeSet(TRACE_KEY, val);
            setCookie(TRACE_COOKIE, val);
        }
    };

    const initTrace = (incomingTraceId) => {
        if (state.traceId) return state.traceId;
        const stored = safeGet(TRACE_KEY) || readCookie(TRACE_COOKIE);
        const trace = incomingTraceId || stored || genId();
        setTraceId(trace);
        return trace;
    };

    const updateFromResponse = (resp) => {
        const t = resp.headers.get("X-Trace-Id");
        const s = resp.headers.get("X-Span-Id");
        if (t && !state.traceId) {
            setTraceId(t);
        }
        return { traceId: state.traceId || t, spanId: s || "" };
    };

    const log = (level, eventName, data = {}) => {
        const traceId = state.traceId || initTrace();
        const entry = {
            timestamp: new Date().toISOString(),
            level: level || "info",
            event: eventName,
            traceId,
            spanId: data.spanId || "",
            data
        };
        push(entry);
    };

    const startSpan = (name, data = {}) => {
        const spanId = `${(++state.spanSeed).toString(16)}${Date.now().toString(16)}`;
        const start = performance.now();
        log("info", `${name}.start`, { ...data, spanId });
        return {
            spanId,
            end: (status = "ok", extra = {}) => {
                const durationMs = Math.round(performance.now() - start);
                log(status === "error" ? "error" : "info", `${name}.end`, { ...data, ...extra, durationMs, spanId, status });
            }
        };
    };

    const fetchWithTrace = async (url, options = {}, meta = {}) => {
        const traceId = initTrace();
        const span = startSpan(meta.event || "fetch", { url, ...meta });
        const headers = new Headers(options.headers || {});
        headers.set("X-Trace-Id", traceId);
        headers.set("X-Span-Id", span.spanId);
        headers.set("X-Requested-With", "XMLHttpRequest");
        const merged = { ...options, headers };

        try {
            const resp = await fetch(url, merged);
            const { spanId } = updateFromResponse(resp);
            const summary = resp.headers.get("X-Trace-Summary");
            span.end(resp.ok ? "ok" : "error", { statusCode: resp.status, spanId, summary });
            return resp;
        } catch (err) {
            span.end("error", { error: err?.message });
            throw err;
        }
    };

    const getBuffer = () => [...state.buffer];
    const getContext = () => ({ traceId: state.traceId, lastSpan: state.spanSeed });
    const clearBuffer = () => { state.buffer = []; safeRemove(BUFFER_KEY); };
    const clearTrace = () => { state.traceId = null; safeRemove(TRACE_KEY); };

    window.scheduleLogger = {
        initTrace,
        updateFromResponse,
        log,
        startSpan,
        fetchWithTrace,
        getBuffer,
        getContext,
        clearBuffer,
        clearTrace
    };
})();
