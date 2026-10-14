// A client for IDrive e2's reseller API: the control side of Uncloud Backup. It makes and removes
// the e2 accounts households back up into, gives them storage in a region, and hands out the keys
// their hosts use. Nothing here touches anybody's files; that is S3's job, with a key this made.
//
// The token it carries opens every customer's storage. It belongs on a server Uncloud runs and
// nowhere else: never in the app, never on a host.

export const RESELLER_API = "https://api.idrivee2.com/api/reseller/v1";

/** An answer in the API's own error shape, `{ error: { type, code, message } }`. */
export class ResellerError extends Error {
  constructor(method, path, status, body) {
    const error = body && typeof body === "object" ? body.error : undefined;
    const said = error ? `: ${error.code ?? error.type ?? "error"}, ${error.message ?? "no message"}` : "";
    super(`${method} ${path} answered ${status}${said}`);
    this.name = "ResellerError";
    this.status = status;
    this.code = error?.code;
    this.type = error?.type;
    this.body = body;
  }
}

/**
 * The reseller API, one method per endpoint the spike uses. `onCall` hears about every call,
 * failed ones included, with what was sent and what came back; the caller decides what to keep.
 */
export function createReseller({ token, baseUrl = RESELLER_API, fetchImpl = fetch, onCall } = {}) {
  if (!token) throw new Error("No reseller token.");

  async function call(method, path, body) {
    const started = performance.now();
    let status = 0;
    let answer = null;
    try {
      const response = await fetchImpl(baseUrl + path, {
        method,
        headers: {
          token,
          accept: "application/json",
          ...(body === undefined ? {} : { "content-type": "application/json" }),
        },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      status = response.status;
      answer = parse(await response.text());
      // Errors come back with an `error` object. One that arrives as a 200 is an error all the same.
      if (!response.ok || (answer && typeof answer === "object" && answer.error)) {
        throw new ResellerError(method, path, status, answer);
      }
      return answer;
    } finally {
      onCall?.({ method, path, request: body, status, response: answer, ms: Math.round(performance.now() - started) });
    }
  }

  return {
    regions: () => call("GET", "/regions"),
    users: (email) => call("GET", email ? `/users?email=${encodeURIComponent(email)}` : "/users"),
    // The API takes the password base64-encoded.
    createUser: ({ password, ...user }) =>
      call("PUT", "/create_user", { ...user, password: Buffer.from(password, "utf8").toString("base64") }),
    editUser: (changes) => call("POST", "/edit_user", changes),
    disableUser: (email) => call("POST", "/disable_user", { email }),
    enableUser: (email) => call("POST", "/enable_user", { email }),
    removeUser: (email) => call("POST", "/remove_user", { email }),
    enableUserRegion: (email, region) => call("POST", "/enable_user_region", { email, region }),
    listUserRegions: (email) => call("POST", "/list_user_regions", { email }),
    createAccessKey: (options) => call("POST", "/create_access_key", options),
    listAccessKeys: (email, storageDn) => call("POST", "/list_access_keys", { email, storage_dn: storageDn }),
    removeAccessKey: (email, storageDn, accessKey) =>
      call("POST", "/remove_access_key", { email, storage_dn: storageDn, access_key: accessKey }),
    usageStats: (email, from, to) => call("POST", "/usage_stats", { email, date_from: from, date_to: to }),
    bucketStats: (email, storageDn, bucket) =>
      call("POST", "/storage/bucket_stats", { email, storage_dn: storageDn, bucket_name: bucket }),
  };
}

function parse(text) {
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return { raw: text.slice(0, 2000) };
  }
}

/**
 * The list in an answer, whether the API sent it bare, under `key`, under `data`, or under some
 * other name. Several answers' shapes aren't documented, so this doesn't insist on one.
 */
export function listIn(answer, key) {
  if (Array.isArray(answer)) return answer;
  if (!answer || typeof answer !== "object") return [];
  if (Array.isArray(answer[key])) return answer[key];
  if (Array.isArray(answer.data)) return answer.data;
  if (answer.data && Array.isArray(answer.data[key])) return answer.data[key];
  return Object.values(answer).find(Array.isArray) ?? [];
}

/**
 * The `storage_dn` for `region` in an answer from list_user_regions, wherever it sits. The docs
 * show that answer only as a placeholder.
 */
export function findStorageDn(answer, region) {
  const found = [];
  (function walk(value) {
    if (Array.isArray(value)) value.forEach(walk);
    else if (value && typeof value === "object") {
      if (typeof value.storage_dn === "string") found.push(value);
      Object.values(value).forEach(walk);
    }
  })(answer);
  const match = region ? found.find((entry) => Object.values(entry).includes(region)) : undefined;
  if (match ?? found[0]) return (match ?? found[0]).storage_dn;
  // Or a map from region to address.
  const byRegion = answer?.user_regions?.[region];
  return typeof byRegion === "string" ? byRegion : undefined;
}
