import { useCallback, useState } from "react";

export type Theme = "light" | "dark";

const KEY = "uncloud.theme";

/** The theme this browser was last left on. Light unless somebody chose dark here. */
export function savedTheme(): Theme {
  try {
    return window.localStorage.getItem(KEY) === "dark" ? "dark" : "light";
  } catch {
    // Storage can be off (a private window, a locked-down browser); light it is.
    return "light";
  }
}

/** Matches --bg in styles.css, for the browser chrome around the page. */
const themeColor: Record<Theme, string> = { light: "#ffffff", dark: "#16181c" };

/** Every colour is a token that data-theme on <html> switches, so this is the whole change. */
export function applyTheme(theme: Theme) {
  document.documentElement.dataset.theme = theme;
  document.querySelector('meta[name="theme-color"]')?.setAttribute("content", themeColor[theme]);
}

/** The current theme, and a way to flip it that this browser remembers. */
export function useTheme() {
  const [theme, setTheme] = useState<Theme>(savedTheme);
  const toggle = useCallback(() => {
    setTheme((current) => {
      const next: Theme = current === "dark" ? "light" : "dark";
      applyTheme(next);
      try {
        window.localStorage.setItem(KEY, next);
      } catch {
        // Not remembered, but still switched for as long as the page is open.
      }
      return next;
    });
  }, []);
  return { theme, toggle };
}
