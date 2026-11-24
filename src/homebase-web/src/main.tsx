import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
// Latin comes from ./fonts, where the zero's slash runs the usual way; the rest as packaged.
import "@fontsource/atkinson-hyperlegible-next/latin-ext-400.css";
import "@fontsource/atkinson-hyperlegible-next/latin-ext-600.css";
import "@fontsource/atkinson-hyperlegible-next/latin-ext-700.css";
import "./fonts/fonts.css";
import App from "./App";
import SharedPage from "./SharedPage";
import { applyTheme, savedTheme } from "./theme";
import "./styles.css";

// Before the first paint, so somebody who chose dark doesn't get a flash of light first.
applyTheme(savedTheme());

// A link somebody shared is its own page, for whoever has it, with or without an account here.
const shared = /^\/s\/([A-Za-z0-9_-]+)\/?$/.exec(window.location.pathname);

createRoot(document.getElementById("root")!).render(
  <StrictMode>{shared ? <SharedPage token={shared[1]} /> : <App />}</StrictMode>,
);
