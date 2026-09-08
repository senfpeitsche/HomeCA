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

window.homeca.addHelpCopyButtons = (label) => {
  document.querySelectorAll(".help-content pre").forEach(pre => {
    if (pre.querySelector(".help-copy-button")) return;
    const code = pre.querySelector("code");
    if (!code) return;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "help-copy-button";
    button.setAttribute("aria-label", label);
    button.title = label;
    button.textContent = "⧉";
    button.addEventListener("click", async () => {
      await window.homeca.copyToClipboard(code.innerText);
    });
    pre.appendChild(button);
  });
};

// Per-browser preference. Guarded: private browsing can throw on access.
window.homeca.getThemePreference = () => {
  try {
    return localStorage.getItem("homeca.theme");
  } catch {
    return null;
  }
};

window.homeca.setThemePreference = (preference) => {
  try {
    localStorage.setItem("homeca.theme", preference);
  } catch { }
};

window.homeca.setCulture = (culture) => {
  // ASP.NET Core's standard request-culture cookie. Reloading creates a new
  // Blazor circuit with the selected culture rather than mutating a live one.
  document.cookie = `.AspNetCore.Culture=c=${culture}|uic=${culture}; path=/; max-age=31536000; samesite=lax`;
  window.location.reload();
};

window.homeca.clearBrowserSession = async () => {
  await fetch("/api/v1/ui-session/logout", { method: "POST", credentials: "same-origin" });
};

window.homeca.persistBrowserSession = async (token) => {
  await fetch("/api/v1/ui-session", {
    method: "POST",
    credentials: "same-origin",
    headers: { Authorization: `Bearer ${token}` }
  });
};
