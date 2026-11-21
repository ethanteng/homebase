import { describe, expect, it } from "vitest";
import { suggestPassword } from "./UsersPanel";
import { WORDS } from "./words";

/**
 * The password suggested when adding a person. An admin can accept it as it is, and nothing makes
 * the person change it later, so it has to be strong on its own — strong enough that copied
 * password hashes couldn't simply be tried against every suggestion there is.
 */
describe("suggested passwords", () => {
  it("come from enough combinations to resist guessing offline", () => {
    const bits = 4 * Math.log2(WORDS.length) + Math.log2(900);
    expect(bits).toBeGreaterThanOrEqual(50);
  });

  it("use only distinct, short, lowercase words", () => {
    expect(new Set(WORDS).size).toBe(WORDS.length);
    for (const word of WORDS) expect(word).toMatch(/^[a-z]{3,8}$/);
  });

  it("are four listed words and a three-digit number", () => {
    for (let i = 0; i < 200; i++) {
      const password = suggestPassword();
      const parts = password.split("-");
      expect(parts).toHaveLength(5);
      for (const word of parts.slice(0, 4)) expect(WORDS).toContain(word);
      expect(Number(parts[4])).toBeGreaterThanOrEqual(100);
      expect(Number(parts[4])).toBeLessThanOrEqual(999);
      // The server's own minimum and maximum, so a suggestion is never turned away.
      expect(password.length).toBeGreaterThanOrEqual(10);
      expect(password.length).toBeLessThanOrEqual(256);
    }
  });
});
