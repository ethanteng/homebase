import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
// Latin comes from ./fonts, where the zero's slash runs the usual way; the rest as packaged.
import "@fontsource/atkinson-hyperlegible-next/latin-ext-400.css";
import "@fontsource/atkinson-hyperlegible-next/latin-ext-600.css";
import "@fontsource/atkinson-hyperlegible-next/latin-ext-700.css";
import "./fonts/fonts.css";
import App from "./App";
import { applyTheme, savedTheme } from "./theme";
import "./styles.css";

// Before the first paint, so somebody who chose dark doesn't get a flash of light first.
applyTheme(savedTheme());

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
