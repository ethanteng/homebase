import { useState } from "react";
import { Check, Copy, ExternalLink, ShieldCheck } from "lucide-react";

const APP_CONSOLE = "https://www.dropbox.com/developers/apps";

interface Props {
  redirectUri: string;
  scopes: string[];
  idPrefix: string;
}

/**
 * How to make a Dropbox app, and the address to register with it. The same four steps whether the
 * key being set up is one account's own or the one the host offers everybody, so they are written
 * once and shown in both places.
 */
export default function DropboxAppSteps({ redirectUri, scopes, idPrefix }: Props) {
  const [copied, setCopied] = useState(false);

  async function copyRedirect() {
    try {
      await navigator.clipboard.writeText(redirectUri);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      // Clipboard access can be refused; the address is on screen to select by hand.
    }
  }

  return (
    <ol className="setup-steps" id={`${idPrefix}-steps`}>
      <li>
        <a href={APP_CONSOLE} target="_blank" rel="noreferrer noopener">
          Create an app on dropbox.com
          <ExternalLink size={13} />
        </a>{" "}
        — choose <strong>Scoped access</strong> and <strong>Full Dropbox</strong>.
      </li>
      <li>
        On its <strong>Permissions</strong> tab, tick{" "}
        {scopes.map((scope, index) => (
          <span key={scope}>
            {index > 0 ? ", " : ""}
            <code>{scope}</code>
          </span>
        ))}
        , then <strong>Submit</strong>. These are read-only: Uncloud cannot change
        anything in anybody’s Dropbox.
      </li>
      <li>
        On its <strong>Settings</strong> tab, add this exact{" "}
        <strong>Redirect URI</strong>:
        <span className="copy-row">
          <code>{redirectUri}</code>
          <button
            type="button"
            className="icon-button"
            onClick={() => void copyRedirect()}
            aria-label="Copy the redirect address"
            title="Copy"
          >
            {copied ? <Check size={15} /> : <Copy size={15} />}
          </button>
        </span>
      </li>
      <li>
        Copy that app’s <strong>App key</strong> into the box below.
      </li>
    </ol>
  );
}

export function AppKeyReassurance() {
  return (
    <>
      <ShieldCheck size={14} /> An app key isn’t a password. It only lets Uncloud ask Dropbox for
      permission: each person still signs in to their own Dropbox, and Uncloud can only read it,
      never change it.
    </>
  );
}
