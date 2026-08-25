import { readFileSync } from "node:fs";
import assert from "node:assert/strict";
import test from "node:test";

const appSource = readFileSync(
  new URL("../host/ClassroomLive.Host/wwwroot/app.js", import.meta.url),
  "utf8",
);
const functionBlock = appSource.match(
  /function studentRateLimit\(response\) \{[\s\S]*?\r?\n  \}/,
);
assert.ok(functionBlock, "app.js에서 studentRateLimit 함수를 찾지 못했습니다.");

const studentRateLimit = new Function(`${functionBlock[0]}\nreturn studentRateLimit;`)();
const response = (status, pinLocked = false) => ({
  status,
  headers: { get: () => pinLocked ? "1" : null },
});

test("PIN 잠금과 일반 트래픽 제한을 구분한다", () => {
  assert.equal(studentRateLimit(response(429, true)), "pin-locked");
  assert.equal(studentRateLimit(response(429)), "retry");
  assert.equal(studentRateLimit(response(401)), "");
});
