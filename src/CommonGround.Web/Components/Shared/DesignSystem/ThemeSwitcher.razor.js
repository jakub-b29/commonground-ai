export function getStoredTheme() {
    try {
        const saved = localStorage.getItem('propai-theme');
        if (saved === 'light' || saved === 'dark') {
            document.documentElement.setAttribute('data-theme', saved);
            return saved;
        }
    } catch {
        // Ignore storage errors in restricted environments
    }
    const current = document.documentElement.getAttribute('data-theme');
    return current === 'light' ? 'light' : 'dark';
}

export function applyTheme(theme) {
    const normalized = theme === 'light' ? 'light' : 'dark';
    const root = document.documentElement;
    root.classList.add('theme-transitioning');
    root.setAttribute('data-theme', normalized);
    try {
        localStorage.setItem('propai-theme', normalized);
    } catch {
        // Ignore storage errors
    }
    requestAnimationFrame(() => {
        requestAnimationFrame(() => {
            root.classList.remove('theme-transitioning');
        });
    });
    return normalized;
}

