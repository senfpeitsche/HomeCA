window.homeca = window.homeca || {};
window.homeca.download = (fileName, contentType, base64) => {
  const bytes = Uint8Array.from(atob(base64), character => character.charCodeAt(0));
  const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
};

window.homeca.redirectAfter = (url, delayMilliseconds) => {
  window.setTimeout(() => window.location.assign(url), delayMilliseconds);
};

window.homeca.copyToClipboard = async (text) => {
  // Try modern Clipboard API first (works on HTTPS / localhost)
  if (navigator.clipboard && navigator.clipboard.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch { }
  }
  // Fallback for HTTP: use a temporary textarea + execCommand
  const textarea = document.createElement("textarea");
  textarea.value = text;
  textarea.style.position = "fixed";
  textarea.style.opacity = "0";
  document.body.appendChild(textarea);
  textarea.select();
  try {
    document.execCommand("copy");
    return true;
  } catch {
    return false;
  } finally {
    document.body.removeChild(textarea);
  }
};
