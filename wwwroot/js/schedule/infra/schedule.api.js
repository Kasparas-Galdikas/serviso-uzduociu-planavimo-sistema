/**
 * schedule.api.js
 *
 * Centralized API client for schedule-related HTTP requests.
 * Provides consistent headers, error handling, and tracing integration.
 *
 * Dependencies: schedule.logger (optional), schedule.grid (for timezone)
 * Dependents: task.api, schedule.selection, schedule.dragdrop
 */
(() => {
  "use strict";

  window.schedule = window.schedule || {};

  const logger = window.scheduleLogger;
  const grid = window.schedule.grid;

  /**
   * Get common headers for API requests.
   * @returns {Object} Headers object
   */
  const getCommonHeaders = () => {
    const headers = {
      "X-Requested-With": "XMLHttpRequest",
      "Content-Type": "application/json",
    };

    // Add timezone offset if grid module is available
    const timezoneOffset = grid?.getTimezoneOffset?.() ?? -new Date().getTimezoneOffset();
    headers["X-Timezone-Offset"] = String(timezoneOffset);

    return headers;
  };

  /**
   * Make an API request with tracing and common headers.
   * @param {string} url - The URL to fetch
   * @param {Object} options - Fetch options
   * @param {Object} [meta] - Metadata for logging
   * @returns {Promise<Response>} The fetch response
   */
  const request = async (url, options = {}, meta = {}) => {
    const headers = {
      ...getCommonHeaders(),
      ...options.headers,
    };

    const fetchOptions = {
      ...options,
      headers,
    };

    // Use logger's fetchWithTrace if available for distributed tracing
    if (logger?.fetchWithTrace) {
      return logger.fetchWithTrace(url, fetchOptions, meta);
    }

    return fetch(url, fetchOptions);
  };

  /**
   * Make a GET request.
   * @param {string} url - The URL to fetch
   * @param {Object} [options] - Additional fetch options
   * @param {Object} [meta] - Metadata for logging
   * @returns {Promise<Response>}
   */
  const get = (url, options = {}, meta = {}) => {
    return request(url, { ...options, method: "GET" }, { ...meta, method: "GET" });
  };

  /**
   * Make a POST request with JSON body.
   * @param {string} url - The URL to fetch
   * @param {Object} body - The request body (will be JSON stringified)
   * @param {Object} [options] - Additional fetch options
   * @param {Object} [meta] - Metadata for logging
   * @returns {Promise<Response>}
   */
  const post = (url, body, options = {}, meta = {}) => {
    return request(
      url,
      { ...options, method: "POST", body: JSON.stringify(body) },
      { ...meta, method: "POST" }
    );
  };

  /**
   * Make a PATCH request with JSON body.
   * @param {string} url - The URL to fetch
   * @param {Object} body - The request body (will be JSON stringified)
   * @param {Object} [options] - Additional fetch options
   * @param {Object} [meta] - Metadata for logging
   * @returns {Promise<Response>}
   */
  const patch = (url, body, options = {}, meta = {}) => {
    return request(
      url,
      { ...options, method: "PATCH", body: JSON.stringify(body) },
      { ...meta, method: "PATCH" }
    );
  };

  /**
   * Make a DELETE request with optional JSON body.
   * @param {string} url - The URL to fetch
   * @param {Object} [body] - The request body (will be JSON stringified)
   * @param {Object} [options] - Additional fetch options
   * @param {Object} [meta] - Metadata for logging
   * @returns {Promise<Response>}
   */
  const del = (url, body = null, options = {}, meta = {}) => {
    const fetchOptions = { ...options, method: "DELETE" };
    if (body) {
      fetchOptions.body = JSON.stringify(body);
    }
    return request(url, fetchOptions, { ...meta, method: "DELETE" });
  };

  /**
   * Parse a JSON response, returning null on failure.
   * @param {Response} response - The fetch response
   * @returns {Promise<Object|null>}
   */
  const parseJson = async (response) => {
    try {
      return await response.json();
    } catch {
      return null;
    }
  };

  /**
   * Handle common API error responses.
   * @param {Response} response - The fetch response
   * @param {Object} [options] - Options for error handling
   * @param {boolean} [options.showAlert] - Whether to show alert for errors
   * @returns {Promise<{ ok: boolean, status: number, data: Object|null, message: string|null }>}
   */
  const handleResponse = async (response, options = {}) => {
    const { showAlert = false } = options;
    const data = await parseJson(response);

    if (response.ok) {
      return { ok: true, status: response.status, data, message: null };
    }

    // Extract error message
    let message = null;
    if (data?.message) {
      message = data.message;
    } else if (data?.errors) {
      // Validation errors - get first error message
      const firstError = Object.values(data.errors).flat()[0];
      message = firstError || "Patikrinkite įvestus duomenis.";
    } else {
      // Default messages by status code
      switch (response.status) {
        case 400:
          message = "Netinkami duomenys.";
          break;
        case 403:
          message = "Neturite teisių atlikti šį veiksmą.";
          break;
        case 404:
          message = "Įrašas nerastas.";
          break;
        case 409:
          message = "Konfliktas - duomenys pasikeitė.";
          break;
        case 422:
          message = "Nepavyko apdoroti užklausos.";
          break;
        default:
          message = "Įvyko klaida.";
      }
    }

    if (showAlert && message) {
      alert(message);
    }

    return { ok: false, status: response.status, data, message };
  };

  /**
   * Extract validation errors from a response.
   * @param {Object} data - Parsed response data
   * @returns {Object|null} Field -> message mapping or null
   */
  const extractValidationErrors = (data) => {
    if (!data?.errors || typeof data.errors !== "object") {
      return null;
    }

    const errors = {};
    for (const [field, messages] of Object.entries(data.errors)) {
      errors[field] = Array.isArray(messages) ? messages[0] : messages;
    }
    return errors;
  };

  // Export module
  window.schedule.api = {
    request,
    get,
    post,
    patch,
    delete: del,
    parseJson,
    handleResponse,
    extractValidationErrors,
    getCommonHeaders,
  };
})();