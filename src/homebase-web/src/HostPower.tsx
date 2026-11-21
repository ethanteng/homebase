import { useEffect, useState } from "react";
import { api } from "./api";
import type { HostPower as Report } from "./api";
import { SettingsGroup, StatusLine } from "./Settings";

/**
 * What could stop this host staying on for everybody: going to sleep, a laptop's lid, its battery,
 * a power cut, FileVault after a restart. Whoever looks after Uncloud is usually somewhere else when
 * that matters, so it's said here as well as in the menu on the host. Nothing here changes anything:
 * what Uncloud can put right, it puts right from that menu, where macOS can ask for a password.
 */
export default function HostPower() {
  const [report, setReport] = useState<Report | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    api<Report>("/host/power", { signal: controller.signal })
      .then(setReport)
      .catch(() => {
        // The rest of the settings are still worth showing without this.
      });
    return () => controller.abort();
  }, []);

  // Nothing to say on a host that isn't a Mac, which has none of this to read.
  if (!report?.checked) return null;

  return (
    <SettingsGroup title="This Mac" note="Files are only available while it’s on.">
      {report.findings.length === 0 ? (
        <StatusLine tone="ok">Nothing should stop it staying on.</StatusLine>
      ) : (
        <ul className="status-list">
          {report.findings.map((finding) => (
            <li key={`${finding.code} ${finding.title}`}>
              {/* FileVault is worth knowing about, not fixing: it's what keeps the files safe. */}
              <StatusLine tone={finding.code === "filevault" ? "info" : "warn"}>
                {finding.title}
                <small>{finding.message}</small>
              </StatusLine>
            </li>
          ))}
        </ul>
      )}
    </SettingsGroup>
  );
}
