import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "@fontsource/atkinson-hyperlegible-next/400.css";
import "@fontsource/atkinson-hyperlegible-next/600.css";
import "@fontsource/atkinson-hyperlegible-next/700.css";
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
