import { timingSafeEqual } from "node:crypto";
import { database, ready } from "./db.mjs";
import { connectIdrive } from "./idrive.mjs";

/** A failure with an answer of its own: a status, a code a program can act on, and words for a person. */
export class ServiceError extends Error {
  constructor(status, code, message) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

export function json(status, body, headers = {}) {
  return Response.json(body, { status, headers: { "cache-control": "no-store", ...headers } });
}

/**
 * Answers a request with `handler`, given what it needs: the database, brought up to date, iDrive,
 * the environment and the time. A ServiceError becomes its own answer; anything else is logged and
 * answered as the service's fault, without saying what happened.
 */
export function route(handler) {
  return async (request) => {
    try {
      const db = database();
      return await handler(request, {
        db: db ? await ready(db) : null,
        idrive: connectIdrive(process.env),
        env: process.env,
        now: new Date(),
      });
    } catch (error) {
      return failure(error);
    }
  };
}

export function failure(error) {
  if (error instanceof ServiceError) return json(error.status, { error: { code: error.code, message: error.message } });
  console.error(error);
  return json(500, { error: { code: "internal", message: "Something went wrong in Uncloud Backup's service." } });
}

export function needDatabase(db) {
  if (!db) throw new ServiceError(503, "no_database", "This deployment of the service has no database.");
  return db;
}

export function needIdrive(idrive) {
  if (!idrive) throw new ServiceError(503, "no_idrive", "This deployment of the service can't reach iDrive.");
  return idrive;
}

export async function body(request) {
  try {
    const parsed = await request.json();
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) return parsed;
  } catch {
    // Answered below, the same as a body that's JSON but not an object.
  }
  throw new ServiceError(400, "bad_request", "The request needs a JSON object.");
}

/** The bearer token a request carries, or null. */
export function bearer(request) {
  const match = /^Bearer (\S+)$/.exec(request.headers.get("authorization") ?? "");
  return match ? match[1] : null;
}

/**
 * Whether a request carries `secret` as its bearer token. With no secret set, nothing does: a secret
 * left unset must lock the door, not open it.
 */
export function carries(request, secret) {
  if (!secret) return false;
  const given = Buffer.from(request.headers.get("authorization") ?? "");
  const expected = Buffer.from(`Bearer ${secret}`);
  return given.length === expected.length && timingSafeEqual(given, expected);
}
