import { ready } from "./db.mjs";

// Whether the service is set up, for whoever is setting it up: which of what it needs is in place,
// whether its database answers, and whether its tables are up to date. It's open to anybody, so it
// says whether each thing is there and never what it is.

/**
 * @param {Record<string, string | undefined>} env
 * @param {{ query: Function } | null} db the database, or null when there's none.
 * @param {{ deadline?: number }} [options] how long each look at the database may take, in milliseconds.
 */
export async function checkHealth(env, db, { deadline = 8_000 } = {}) {
  const configured = {
    resellerToken: Boolean(env.IDRIVE_RESELLER_TOKEN),
    operatorToken: Boolean(env.OPERATOR_TOKEN),
    cronSecret: Boolean(env.CRON_SECRET),
    database: Boolean(db),
  };
  let database = "not set up";
  let tables = null;
  if (db) {
    database = (await within(deadline, () => db.query("select 1"), "The database didn't answer")) ? "answers" : "doesn't answer";
    if (database === "answers") {
      tables = (await within(deadline, () => ready(db), "The tables couldn't be brought up to date"))
        ? "current"
        : "couldn't be brought up to date";
    }
  }
  return {
    service: "uncloud-backup",
    ready: Object.values(configured).every(Boolean) && tables === "current",
    configured,
    database,
    tables,
  };
}

/**
 * Whether `work` finished in time. A deadline of its own, so an outage is reported as one however
 * the driver behaves. What went wrong can name the database's host, so it goes to the log, not out.
 */
async function within(deadline, work, saying) {
  let timer;
  try {
    await Promise.race([
      work(),
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`No answer within ${deadline} ms`)), deadline);
      }),
    ]);
    return true;
  } catch (error) {
    console.error(`${saying}:`, error?.message ?? error);
    return false;
  } finally {
    clearTimeout(timer);
  }
}
