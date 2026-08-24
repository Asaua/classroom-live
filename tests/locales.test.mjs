import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";

const directory = path.resolve("locales");
const files = fs.readdirSync(directory).filter((name) => name.endsWith(".json")).sort();
const catalogs = files.map((name) => [name, JSON.parse(fs.readFileSync(path.join(directory, name), "utf8"))]);
const placeholders = (value) => [...String(value).matchAll(/\{([a-z][a-z0-9]*)\}/gi)]
  .map((match) => match[1]).sort();

test("locale catalogs share keys and placeholders", () => {
  assert.ok(catalogs.length >= 2);
  const [baseName, base] = catalogs.find(([, catalog]) => catalog.$code === "ko") ?? catalogs[0];
  const keys = Object.keys(base).sort();

  for (const [name, catalog] of catalogs) {
    assert.equal(catalog.$code, path.basename(name, ".json"), `${name}: $code`);
    assert.ok(catalog.$name?.trim(), `${name}: $name`);
    assert.ok(["ltr", "rtl"].includes(catalog.$direction), `${name}: $direction`);
    assert.deepEqual(Object.keys(catalog).sort(), keys, `${name}: keys differ from ${baseName}`);
    for (const key of keys) {
      assert.equal(typeof catalog[key], "string", `${name}: ${key} must be a string`);
      assert.ok(catalog[key].trim(), `${name}: ${key} is empty`);
      assert.deepEqual(placeholders(catalog[key]), placeholders(base[key]), `${name}: ${key} placeholders`);
    }
  }
});

test("literal UI keys exist in the catalog", () => {
  const base = catalogs.find(([, catalog]) => catalog.$code === "ko")[1];
  const sources = [
    "host/ClassroomLive.Host/wwwroot/app.js",
    "host/ClassroomLive.Host/wwwroot/index.html",
    "extension/ClassroomLive.Extension/ClassroomLivePackage.cs",
    "extension/ClassroomLive.Extension/LanguageSelectionDialog.cs",
  ].map((name) => fs.readFileSync(path.resolve(name), "utf8")).join("\n");
  const keys = [...sources.matchAll(/(?:\bt|\bL|ExtensionLocalization\.T)\(\s*["'`]([^"'`]+)["'`]/g)]
    .map((match) => match[1])
    .concat([...sources.matchAll(/data-i18n(?:-title|-aria)?=["']([^"']+)["']/g)].map((match) => match[1]));
  for (const key of new Set(keys.filter((value) => !value.includes("${"))))
    assert.ok(key in base, `missing locale key: ${key}`);
});
