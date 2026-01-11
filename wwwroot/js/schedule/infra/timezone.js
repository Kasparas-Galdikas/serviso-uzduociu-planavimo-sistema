// Set timezone offset cookie on page load so server can access it
(function() {
  const timezoneOffsetMinutes = -new Date().getTimezoneOffset();
  const cookieName = 'timezone_offset';

  // Check if cookie already exists and has the correct value
  const existingCookie = document.cookie
    .split('; ')
    .find(row => row.startsWith(cookieName + '='));

  const currentValue = existingCookie ? existingCookie.split('=')[1] : null;

  // Only update if value changed or doesn't exist
  if (currentValue !== String(timezoneOffsetMinutes)) {
    document.cookie = `${cookieName}=${timezoneOffsetMinutes}; path=/; max-age=31536000`; // 1 year
  }
})();
