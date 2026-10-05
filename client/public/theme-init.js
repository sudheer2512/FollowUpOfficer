// Applies a stored theme before first paint (external file so the strict Content-Security-Policy can forbid inline scripts).
try {
  var t = localStorage.getItem('fu.theme');
  if (t === 'light' || t === 'dark') document.documentElement.setAttribute('data-theme', t);
} catch (e) {}
