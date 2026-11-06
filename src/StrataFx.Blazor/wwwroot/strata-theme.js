// Remembers the user's theme choice in this browser.
const key = "stratafx-theme";

export function load() {
    try {
        return JSON.parse(localStorage.getItem(key));
    } catch {
        return null;
    }
}

export function save(theme, mode) {
    try {
        localStorage.setItem(key, JSON.stringify({ theme, mode }));
    } catch {
        // Storage can be unavailable (private windows, blocked site data); the choice then lasts for the session.
    }
}
