// Whether the service is set up, for whoever is setting it up: which of what it needs is in place,
// and whether its database answers. It's open to anybody, so it says whether each thing is there and
// never what it is.

/**
 * @param {Record<string, string | undefined>} env
 * @param {((text: string) => Promise<unknown>) | null} query the database, or null when there's none.
 */
export async function checkHealth(env, query) {
  const configured = {
    resellerToken: Boolean(env.IDRIVE_RESELLER_TOKEN),
    database: Boolean(query),
    cronSecret: Boolean(env.CRON_SECRET),
  };
  let database = "not set up";
  if (query) {
    try {
      await query("select 1");
      database = "answers";
    } catch (error) {
      // What went wrong can name the database's host, so it goes to the log and not out.
      console.error("The database didn't answer:", error?.message ?? error);
      database = "doesn't answer";
    }
  }
  return {
    service: "uncloud-backup",
    ready: Object.values(configured).every(Boolean) && database === "answers",
    configured,
    database,
  };
}
