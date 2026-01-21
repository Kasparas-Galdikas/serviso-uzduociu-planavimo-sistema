/**
 * ui.select2.js
 *
 * Select2 dropdown initialization and management utilities.
 * Provides consistent Select2 configuration across all schedule forms.
 *
 * Dependencies: jQuery, Select2 library
 * Dependents: task.form, schedule.filters
 */
(() => {
  "use strict";

  window.schedule = window.schedule || {};
  window.schedule.ui = window.schedule.ui || {};

  /**
   * Check if Select2 is available.
   * @returns {boolean} True if jQuery and Select2 are loaded
   */
  const isAvailable = () => {
    return typeof $ !== "undefined" && $.fn && $.fn.select2;
  };

  /**
   * Check if an element has Select2 initialized.
   * @param {HTMLSelectElement} selectEl - The select element
   * @returns {boolean} True if Select2 is initialized
   */
  const isInitialized = (selectEl) => {
    if (!selectEl || !isAvailable()) return false;
    return $(selectEl).hasClass("select2-hidden-accessible");
  };

  /**
   * Initialize Select2 on a select element.
   * @param {HTMLSelectElement} selectEl - The select element to initialize
   * @param {Object} options - Configuration options
   * @param {string} [options.placeholder] - Placeholder text
   * @param {boolean} [options.allowClear] - Allow clearing selection
   * @param {HTMLElement} [options.dropdownParent] - Parent element for dropdown
   */
  const init = (selectEl, options = {}) => {
    if (!selectEl || !isAvailable()) return;

    const $select = $(selectEl);

    // Destroy existing instance if any
    if (isInitialized(selectEl)) {
      $select.select2("destroy");
    }

    // Determine dropdown parent (offcanvas or body)
    const dropdownParent = options.dropdownParent ||
      selectEl.closest(".offcanvas") ||
      document.body;

    $select.select2({
      theme: "bootstrap-5",
      placeholder: options.placeholder || "Pasirinkite...",
      allowClear: options.allowClear ?? false,
      width: "100%",
      dropdownParent: $(dropdownParent),
      language: {
        noResults: () => "Nerasta rezultatų",
        searching: () => "Ieškoma...",
        inputTooShort: (args) => `Įveskite bent ${args.minimum} simbolius`,
      },
    });
  };

  /**
   * Destroy Select2 instance on an element.
   * @param {HTMLSelectElement} selectEl - The select element
   */
  const destroy = (selectEl) => {
    if (!selectEl || !isAvailable()) return;
    if (isInitialized(selectEl)) {
      $(selectEl).select2("destroy");
    }
  };

  /**
   * Set the value of a Select2 dropdown.
   * @param {HTMLSelectElement} selectEl - The select element
   * @param {string|number} value - The value to set
   */
  const setValue = (selectEl, value) => {
    if (!selectEl || !isAvailable()) return;
    if (isInitialized(selectEl)) {
      $(selectEl).val(value).trigger("change.select2");
    }
  };

  /**
   * Get the current value of a Select2 dropdown.
   * @param {HTMLSelectElement} selectEl - The select element
   * @returns {string|null} The current value or null
   */
  const getValue = (selectEl) => {
    if (!selectEl) return null;
    return selectEl.value || null;
  };

  /**
   * Set the disabled state of a Select2 dropdown.
   * @param {HTMLSelectElement} selectEl - The select element
   * @param {boolean} disabled - Whether to disable the dropdown
   */
  const setDisabled = (selectEl, disabled) => {
    if (!selectEl || !isAvailable()) return;
    if (isInitialized(selectEl)) {
      $(selectEl).prop("disabled", disabled);
    }
  };

  /**
   * Add an option to a select element.
   * @param {HTMLSelectElement} selectEl - The select element
   * @param {Object} option - Option configuration
   * @param {string|number} option.value - Option value
   * @param {string} option.text - Option display text
   * @param {boolean} [option.selected] - Whether option is selected
   * @param {Object} [option.data] - Additional data attributes
   */
  const addOption = (selectEl, { value, text, selected = false, data = {} }) => {
    if (!selectEl) return;

    const opt = document.createElement("option");
    opt.value = value;
    opt.textContent = text;
    if (selected) opt.selected = true;

    // Add data attributes
    Object.entries(data).forEach(([key, val]) => {
      opt.dataset[key] = val;
    });

    selectEl.appendChild(opt);
  };

  /**
   * Clear all options from a select element.
   * @param {HTMLSelectElement} selectEl - The select element
   * @param {boolean} [addPlaceholder] - Whether to add an empty placeholder option
   */
  const clearOptions = (selectEl, addPlaceholder = true) => {
    if (!selectEl) return;
    selectEl.innerHTML = "";

    if (addPlaceholder) {
      const placeholder = document.createElement("option");
      placeholder.value = "";
      placeholder.textContent = "";
      selectEl.appendChild(placeholder);
    }
  };

  /**
   * Refresh Select2 to reflect DOM changes.
   * @param {HTMLSelectElement} selectEl - The select element
   */
  const refresh = (selectEl) => {
    if (!selectEl || !isAvailable()) return;
    if (isInitialized(selectEl)) {
      $(selectEl).trigger("change.select2");
    }
  };

  // Export module
  window.schedule.ui.select2 = {
    isAvailable,
    isInitialized,
    init,
    destroy,
    setValue,
    getValue,
    setDisabled,
    addOption,
    clearOptions,
    refresh,
  };
})();