(() => {
  "use strict";

  const DEFAULTS = {
    success: { delay: 2600, autohide: true, icon: "bi-check-circle-fill", title: "Išsaugota" },
    info: { delay: 3000, autohide: true, icon: "bi-info-circle-fill", title: "Informacija" },
    warning: { delay: 4500, autohide: true, icon: "bi-exclamation-triangle-fill", title: "Dėmesio" },
    error: { delay: 3500, autohide: true, icon: "bi-x-octagon-fill", title: "Klaida" },
  };

  const DEDUPE_WINDOW_MS = 1800;
  const recent = new Map();

  window.schedule = window.schedule || {};
  window.schedule.ui = window.schedule.ui || {};

  const sanitizeMessage = (message, fallback) => {
    const text = typeof message === "string" ? message.trim() : "";
    return text || fallback;
  };

  const getHost = () => {
    let host = document.getElementById("scheduleToastHost");
    if (host) return host;

    host = document.createElement("div");
    host.id = "scheduleToastHost";
    host.className = "toast-container schedule-toast-host position-fixed top-0 end-0 p-3";
    host.setAttribute("aria-live", "polite");
    host.setAttribute("aria-atomic", "true");
    document.body.appendChild(host);
    return host;
  };

  const shouldDeduplicate = (level, message) => {
    const key = `${level}:${message}`;
    const now = Date.now();
    const lastShownAt = recent.get(key) || 0;
    recent.set(key, now);

    for (const [recentKey, shownAt] of recent.entries()) {
      if (now - shownAt > DEDUPE_WINDOW_MS) {
        recent.delete(recentKey);
      }
    }

    return now - lastShownAt < DEDUPE_WINDOW_MS;
  };

  const buildToast = (level, message, options = {}) => {
    const config = { ...DEFAULTS[level], ...options };
    const text = sanitizeMessage(message, options.fallbackMessage || "Ivyko pranesimas.");

    if (shouldDeduplicate(level, text)) {
      return null;
    }

    const toastEl = document.createElement("div");
    toastEl.className = `toast schedule-toast schedule-toast--${level} border-0 shadow-sm`;
    toastEl.setAttribute("role", level === "error" ? "alert" : "status");
    toastEl.setAttribute("aria-live", level === "error" ? "assertive" : "polite");
    toastEl.setAttribute("aria-atomic", "true");
    toastEl.dataset.bsAutohide = String(config.autohide !== false);
    toastEl.dataset.bsDelay = String(config.delay || DEFAULTS[level].delay);

    toastEl.innerHTML = `
      <div class="toast-header schedule-toast__header">
        <i class="bi ${config.icon} schedule-toast__icon" aria-hidden="true"></i>
        <strong class="me-auto">${config.title}</strong>
        <button type="button" class="btn-close" data-bs-dismiss="toast" aria-label="Uzdaryti"></button>
      </div>
      <div class="toast-body">${text}</div>
    `;

    toastEl.addEventListener("hidden.bs.toast", () => {
      toastEl.remove();
    });

    return toastEl;
  };

  const show = (level, message, options = {}) => {
    const toastEl = buildToast(level, message, options);
    if (!toastEl) return null;

    const host = getHost();
    host.appendChild(toastEl);

    if (window.bootstrap?.Toast) {
      window.bootstrap.Toast.getOrCreateInstance(toastEl, {
        autohide: toastEl.dataset.bsAutohide !== "false",
        delay: Number(toastEl.dataset.bsDelay) || DEFAULTS[level].delay,
      }).show();
    } else {
      toastEl.classList.add("show");
      const delay = Number(toastEl.dataset.bsDelay) || DEFAULTS[level].delay;
      window.setTimeout(() => toastEl.remove(), delay);
    }

    return toastEl;
  };

  window.schedule.ui.toast = {
    showSuccess(message, options) {
      return show("success", message, { fallbackMessage: "Veiksmas išsaugotas.", ...options });
    },
    showError(message, options) {
      return show("error", message, { fallbackMessage: "Ivyko klaida.", ...options });
    },
    showWarning(message, options) {
      return show("warning", message, { fallbackMessage: "Patikrinkite veiksmą.", ...options });
    },
    showInfo(message, options) {
      return show("info", message, { fallbackMessage: "Ivyko pranesimas.", ...options });
    },
  };
})();
