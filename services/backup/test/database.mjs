import { PGlite } from "@electric-sql/pglite";

/**
 * A fresh, empty Postgres for one test: PGlite, which is Postgres itself built to run inside Node,
 * in the shape the service's code expects ({ query, exec, transaction }), so the SQL runs for real.
 */
export async function freshDatabase() {
  const lite = await PGlite.create();
  const shaped = (handle) => ({
    query: (text, values) => handle.query(text, values),
    exec: async (sql) => { await handle.exec(sql); },
  });
  return {
    ...shaped(lite),
    transaction: (work) => lite.transaction((tx) => work(shaped(tx))),
    close: () => lite.close(),
  };
}
