// 학생 화면의 구문 강조 토크나이저 검사.
// 의존성 없이 돌린다:  node --test tests/
//
// app.js는 IIFE라 밖에서 부를 수 없으므로 순수 로직(KEYWORDS, tokenize)만
// 떼어내서 확인한다. 여기서 제일 중요한 건 '원문 무손실'이다.
// 토큰을 다시 이었을 때 한 글자라도 잃으면 학생 화면의 코드가 잘려 보인다.
import { readFileSync } from "node:fs";
import assert from "node:assert/strict";
import test from "node:test";

const appSource = readFileSync(
  new URL("../host/ClassroomLive.Host/wwwroot/app.js", import.meta.url),
  "utf8",
);

const keywordsBlock = appSource.match(
  /const KEYWORDS = new Set\(`[\s\S]*?`\.trim\(\)\.split\(\/\\s\+\/\)\);/,
);
const tokenizeBlock = appSource.match(/function tokenize\(text, insideBlock[\s\S]*?\n  \}\n/);
assert.ok(keywordsBlock, "app.js에서 KEYWORDS 블록을 찾지 못했습니다.");
assert.ok(tokenizeBlock, "app.js에서 tokenize 블록을 찾지 못했습니다.");

const { tokenize } = new Function(
  `${keywordsBlock[0]}\n${tokenizeBlock[0]}\nreturn { tokenize };`,
)();

const run = (text, inBlock = false, lineComment = "//", block = true, keywords = true) =>
  tokenize(text, inBlock, lineComment, block, keywords);
const kinds = (result) => result.tokens.map((token) => `${token.kind}:${token.text}`);
const rejoin = (result) => result.tokens.map((token) => token.text).join("");

test("키워드와 식별자를 구분한다", () => {
  assert.deepEqual(kinds(run("public int x")), [
    "keyword:public", "plain: ", "keyword:int", "plain: x",
  ]);
});

test("문자열 안의 //는 주석이 아니다", () => {
  const result = run('var url = "http://x";');
  assert.ok(result.tokens.some((t) => t.kind === "string" && t.text === '"http://x"'));
  assert.ok(!result.tokens.some((t) => t.kind === "comment"));
});

test("주석 안의 따옴표는 문자열이 아니다", () => {
  const result = run('a(); // say "hi');
  assert.deepEqual(
    result.tokens.filter((t) => t.kind === "comment").map((t) => t.text),
    ['// say "hi'],
  );
  assert.ok(!result.tokens.some((t) => t.kind === "string"));
});

test("이스케이프된 따옴표를 문자열 끝으로 보지 않는다", () => {
  const result = run('s = "a\\"b" + c');
  assert.ok(result.tokens.some((t) => t.kind === "string" && t.text === '"a\\"b"'));
});

test("닫히지 않은 문자열도 원문을 잃지 않는다", () => {
  const text = 's = "열린 채 끝남';
  assert.equal(rejoin(run(text)), text);
});

test("블록 주석 상태가 줄을 넘어 이어진다", () => {
  const first = run("code(); /* 시작");
  assert.equal(first.endState, true);

  const middle = run("아직 주석 안", true);
  assert.equal(middle.endState, true);
  assert.deepEqual(kinds(middle), ["comment:아직 주석 안"]);

  const last = run("끝 */ code();", true);
  assert.equal(last.endState, false);
  assert.equal(last.tokens[0].kind, "comment");
  assert.equal(last.tokens[0].text, "끝 */");
});

test("한 줄에서 열고 닫는 블록 주석", () => {
  const result = run("int x /* 설명 */ = 1;");
  assert.equal(result.endState, false);
  assert.ok(result.tokens.some((t) => t.kind === "comment" && t.text === "/* 설명 */"));
  assert.ok(result.tokens.some((t) => t.kind === "number" && t.text === "1"));
});

test("식별자 속 숫자를 숫자로 보지 않는다", () => {
  const result = run("var x2 = 1.5f;");
  assert.ok(result.tokens.some((t) => t.kind === "number" && t.text === "1.5f"));
  assert.ok(!result.tokens.some((t) => t.kind === "number" && t.text.includes("2")));
});

test("Python은 #을 주석으로 본다", () => {
  const result = run("x = 1  # 설명", false, "#", false, true);
  assert.ok(result.tokens.some((t) => t.kind === "comment" && t.text === "# 설명"));
});

test("HTML 등에서는 키워드 강조를 끈다", () => {
  const result = run('<div class="a">', false, "//", false, false);
  assert.ok(!result.tokens.some((t) => t.kind === "keyword"));
});

test("빈 줄", () => {
  const result = run("");
  assert.deepEqual(result.tokens, []);
  assert.equal(result.endState, false);
});

test("탭과 한글이 보존된다", () => {
  const text = '\tvar 이름 = "한글";';
  assert.equal(rejoin(run(text)), text);
});

test("C# 파일 전체를 훑어도 원문이 그대로다", () => {
  const lines = [
    "using System;",
    "",
    "public class Player {",
    "    // 이동 속도 (m/s)",
    "    private float speed = 5.5f;",
    '    public string Name => "플레이어";',
    "    /* 여러 줄",
    "       주석 */",
    "    void Update() { }",
    "}",
  ];

  let state = false;
  for (const line of lines) {
    const result = run(line, state);
    assert.equal(rejoin(result), line, `원문이 바뀌었습니다: ${line}`);
    state = result.endState;
  }
  assert.equal(state, false, "파일 끝에서 블록 주석이 닫혀 있어야 합니다.");
});
