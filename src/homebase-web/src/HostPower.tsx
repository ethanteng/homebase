import { useEffect, useState } from "react";
import { CircleCheck, Power, TriangleAlert } from "lucide-react";
import { api } from "./api";
import type { HostPower as Report } from "./api";

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
    <section className="import-section host-power">
      <div className="import-section-head">
        <h3>
          <Power size={16} />
          Keeping this host on
        </h3>
      </div>
      {report.findings.length === 0 ? (
        <p className="power-clear">
          <CircleCheck size={14} />
          <span>Nothing Uncloud can see should stop this Mac staying on for everyone.</span>
        </p>
      ) : (
        <ul className="power-findings">
          {report.findings.map((finding) => (
            <li key={`${finding.code} ${finding.title}`}>
              <TriangleAlert size={14} />
              <span>{finding.message}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
