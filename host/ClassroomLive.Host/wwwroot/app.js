(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const isHost = location.pathname === "/host";
  const params = new URLSearchParams(location.search);
  const tokenFromUrl = params.get("token");
  const pinFromUrl = params.get("pin");
  // 검은 창을 없앤 뒤로는 토큰이 담긴 주소를 다시 볼 곳이 없다. 교수님이 탭을 닫아도
  // localhost:5050/host 로 돌아올 수 있도록 localStorage에 남긴다.
  // 토큰은 서버를 켤 때마다 새로 만들어지므로 남은 값은 다음 실행에서 그냥 무효가 된다.
  if (tokenFromUrl) localStorage.setItem("classroom-live:admin", tokenFromUrl);
  if (pinFromUrl) sessionStorage.setItem("classroom-live:pin", pinFromUrl);
  if (tokenFromUrl || pinFromUrl) history.replaceState(null, "", location.pathname);

  const adminToken = localStorage.getItem("classroom-live:admin") || "";
  let pin = sessionStorage.getItem("classroom-live:pin") || "";
  let selectedId = localStorage.getItem("classroom-live:selected-file") || "";
  let selectedName = "";
  let latestHostState = null;
  let requestRunning = false;
  let blockedUntil = 0;
  let shuttingDown = false;

  const FONT_STEPS = [11, 12.5, 14, 16, 18, 21, 24];
  const DEFAULT_FONT_INDEX = 1;
  // Number(null)은 0이다. 그대로 넘기면 저장값이 없을 때 가장 작은 단계로 시작한다.
  const storedFont = localStorage.getItem("classroom-live:font");
  let fontIndex = clampFontIndex(storedFont === null ? DEFAULT_FONT_INDEX : Number(storedFont));
  let wrapEnabled = localStorage.getItem("classroom-live:wrap") === "1";
  let following = false;
  let followedLine = 0;

  // 화면에 그려진 줄. { node, text, startState } 로 줄 단위 비교를 한다.
  let renderedRows = [];
  let renderedFileId = "";
  let currentContent = "";
  let noticeTimer = 0;

  // crypto.randomUUID는 보안 컨텍스트(HTTPS 또는 localhost)에서만 존재한다.
  // 학생은 http://192.168.x.x 로 접속하므로 그대로 호출하면 여기서 예외가 나고
  // 아래 코드가 통째로 실행되지 않아 빈 화면만 보인다.
  const newViewerId = () =>
    globalThis.crypto?.randomUUID?.() ??
    `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
  const viewerId = localStorage.getItem("classroom-live:viewer") || newViewerId();
  localStorage.setItem("classroom-live:viewer", viewerId);

  if (isHost) $("hostControls").hidden = false;
  if (!isHost && !pin) showGate("");

  // 한 번 읽으면 끝인 안내다. 교수 화면에는 애초에 필요 없고,
  // 학생도 닫거나 파일을 직접 골라보면 다시 뜨지 않는다.
  if (!isHost && localStorage.getItem("classroom-live:note-read") !== "1")
    $("followNote").hidden = false;
  $("dismissNote").addEventListener("click", dismissNote);
  applyWrap();
  applyFont();

  $("joinForm").addEventListener("submit", (event) => {
    event.preventDefault();
    pin = $("pinInput").value.trim();
    sessionStorage.setItem("classroom-live:pin", pin);
    $("gateError").textContent = "";
    void refresh();
  });

  $("mobileFiles").addEventListener("click", () => openFiles(true));
  $("backdrop").addEventListener("click", () => openFiles(false));

  $("toggleWrap").addEventListener("click", () => {
    wrapEnabled = !wrapEnabled;
    localStorage.setItem("classroom-live:wrap", wrapEnabled ? "1" : "0");
    applyWrap();
  });
  $("fontSmaller").addEventListener("click", () => stepFont(-1));
  $("fontLarger").addEventListener("click", () => stepFont(1));
  $("copyCode").addEventListener("click", async () => {
    if (!currentContent) return notify("복사할 코드가 없어요");
    notify(await copyText(currentContent)
      ? "코드를 복사했어요"
      : "복사하지 못했어요. 직접 선택해 주세요");
  });
  $("followProfessor").addEventListener("click", () => setFollowing(!following));

  $("toggleBroadcast").addEventListener("click", async () => {
    if (!latestHostState) return;
    await fetch("/api/host/broadcast", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Admin-Token": adminToken },
      body: JSON.stringify({ enabled: !latestHostState.broadcasting }),
    });
    await refresh();
  });
  $("copyLink").addEventListener("click", async () => {
    const url = latestHostState?.studentUrls?.[0];
    if (!url) return;
    const button = $("copyLink");
    button.textContent = await copyText(url) ? "복사됨" : "복사 실패";
    setTimeout(() => { button.textContent = "주소 복사"; }, 1200);
  });
  $("toggleShare").addEventListener("click", async () => {
    const button = $("toggleShare");
    const enabled = button.dataset.shared !== "1";
    // 확장자, 크기, 솔루션 밖 여부는 호스트의 보안 규칙이 정한다.
    if (enabled && button.dataset.shareable !== "1")
      return notify("추가할 수 없는 타입의 파일이에요");
    button.disabled = true;
    try {
      const response = await fetch("/api/host/share", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-Admin-Token": adminToken },
        body: JSON.stringify({ enabled }),
      });
      notify(response.ok
        ? (enabled ? "공유했어요" : "공유를 해제했어요")
        : "요청에 실패했어요");
    } catch {
      notify("확장에 연결하지 못했어요");
    } finally {
      button.disabled = false;
    }
    await refresh();
  });

  $("toggleHide").addEventListener("click", async () => {
    const id = $("toggleHide").dataset.fileId;
    if (!id) return;
    await setHidden(id, $("toggleHide").dataset.hidden !== "1");
  });

  $("shutdown").addEventListener("click", async () => {
    const student = latestHostState?.classroom?.viewers ?? 0;
    const warning = student > 0 ? ` 지금 ${student}명이 보고 있어요.` : "";
    if (!await confirmPopup(`수업을 종료할까요?${warning}`, "종료")) return;

    shuttingDown = true;
    try {
      await fetch("/api/host/shutdown", { method: "POST", headers: { "X-Admin-Token": adminToken } });
    } catch { /* 종료 중에 연결이 끊기는 것은 정상이다. */ }
    // 자동으로 사라지면 안 된다. 종료됐다는 사실이 화면에 남아 있어야 한다.
    popup("종료했어요. 탭을 닫아도 돼요", [{ label: "확인" }]);
    setConnection("종료", "paused");
    setText($("hostStatus"), "종료");
  });

  $("allowFirewall").addEventListener("click", async () => {
    const button = $("allowFirewall");
    button.disabled = true;
    button.textContent = "요청 중";
    try {
      const response = await fetch("/api/host/firewall", {
        method: "POST", headers: { "X-Admin-Token": adminToken },
      });
      button.textContent = response.ok ? "허용됨" : "다시 시도";
    } catch {
      button.textContent = "다시 시도";
    } finally {
      button.disabled = false;
    }
  });

  // 숨김은 되돌릴 수 있다. 공유 해제(×)와 달리 목록에는 남는다.
  async function setHidden(id, hidden) {
    try {
      const response = await fetch(`/api/host/files/${encodeURIComponent(id)}/hidden`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-Admin-Token": adminToken },
        body: JSON.stringify({ hidden }),
      });
      notify(response.ok
        ? (hidden ? "학생 화면에서 숨겼어요" : "다시 보이게 했어요")
        : "요청에 실패했어요");
    } catch {
      notify("요청에 실패했어요");
    }
    await refresh();
  }

  function dismissNote() {
    $("followNote").hidden = true;
    localStorage.setItem("classroom-live:note-read", "1");
  }

  function openFiles(open) {
    $("filePanel").classList.toggle("is-open", open);
    $("backdrop").hidden = !open;
  }

  function showGate(message) {
    $("gate").hidden = false;
    $("gateError").textContent = message;
  }

  function setText(element, value) {
    if (element.textContent !== value) element.textContent = value;
  }

  function setTitle(element, value) {
    if (element.getAttribute("title") !== value) element.setAttribute("title", value);
  }

  // 하단 팝업 공통 처리. 알림과 확인을 같은 자리에서 보여준다.
  // actions가 없으면 잠시 뒤 저절로 사라지고, 있으면 사용자가 고를 때까지 남는다.
  function popup(message, actions) {
    const notice = $("notice");
    clearTimeout(noticeTimer);

    const text = document.createElement("span");
    text.className = "notice-text";
    text.textContent = message;
    notice.replaceChildren(text);

    if (actions?.length) {
      const group = document.createElement("span");
      group.className = "notice-actions";
      for (const action of actions) {
        const button = document.createElement("button");
        button.type = "button";
        button.className = `notice-button${action.danger ? " is-danger" : ""}`;
        button.textContent = action.label;
        button.addEventListener("click", () => {
          notice.hidden = true;
          action.select?.();
        });
        group.append(button);
      }
      notice.append(group);
    } else {
      noticeTimer = setTimeout(() => { notice.hidden = true; }, 3200);
    }

    notice.hidden = false;
  }

  const notify = (message) => popup(message, null);

  const confirmPopup = (message, label) => new Promise((resolve) => popup(message, [
    { label: "취소", select: () => resolve(false) },
    { label, danger: true, select: () => resolve(true) },
  ]));

  // 학생은 http로 접속하므로 navigator.clipboard가 없다. crypto.randomUUID와 같은 함정이다.
  async function copyText(text) {
    try {
      if (window.isSecureContext && navigator.clipboard) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch { /* 아래 폴백으로 넘어간다 */ }

    try {
      const area = document.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.style.cssText = "position:fixed;top:0;left:0;width:1px;height:1px;opacity:0";
      document.body.append(area);
      area.select();
      area.setSelectionRange(0, text.length);
      const copied = document.execCommand("copy");
      area.remove();
      return copied;
    } catch {
      return false;
    }
  }

  function clampFontIndex(value) {
    const index = Number.isFinite(value) ? Math.round(value) : DEFAULT_FONT_INDEX;
    return Math.min(FONT_STEPS.length - 1, Math.max(0, index));
  }

  function stepFont(direction) {
    fontIndex = clampFontIndex(fontIndex + direction);
    localStorage.setItem("classroom-live:font", String(fontIndex));
    applyFont();
  }

  function applyFont() {
    $("codeLines").style.setProperty("--code-size", `${FONT_STEPS[fontIndex]}px`);
    $("fontSmaller").disabled = fontIndex === 0;
    $("fontLarger").disabled = fontIndex === FONT_STEPS.length - 1;
  }

  function applyWrap() {
    $("codeLines").classList.toggle("is-wrapped", wrapEnabled);
    const button = $("toggleWrap");
    button.setAttribute("aria-pressed", String(wrapEnabled));
    button.classList.toggle("is-active", wrapEnabled);
  }

  function setFollowing(value) {
    following = value;
    const button = $("followProfessor");
    button.setAttribute("aria-pressed", String(following));
    button.classList.toggle("is-active", following);
    followedLine = 0;
  }

  function scrollToLine(line) {
    const row = renderedRows[line - 1]?.node;
    if (!row) return;
    const scroller = $("codeScroll");
    const target = row.offsetTop - scroller.clientHeight / 3;
    const smooth = !matchMedia("(prefers-reduced-motion: reduce)").matches;
    scroller.scrollTo({ top: Math.max(0, target), behavior: smooth ? "smooth" : "auto" });
  }

  async function refresh() {
    if (requestRunning || shuttingDown) return;
    // 틀린 PIN을 들고 0.75초마다 계속 두드리면 서버의 시도 제한에 스스로 걸린다.
    if (!isHost && !pin) return;
    if (Date.now() < blockedUntil) return;

    requestRunning = true;
    try {
      const response = await fetch(isHost ? "/api/host/state" : "/api/state", {
        cache: "no-store",
        headers: isHost
          ? { "X-Admin-Token": adminToken }
          : { "X-Classroom-Pin": pin, "X-Viewer-Id": viewerId },
      });

      if (!response.ok) {
        if (!isHost && response.status === 429) {
          blockedUntil = Date.now() + 60_000;
          showGate("시도가 너무 많아요. 1분 뒤에 다시 해주세요");
        } else if (!isHost && response.status === 401) {
          pin = "";
          sessionStorage.removeItem("classroom-live:pin");
          showGate("PIN이 맞지 않아요");
        }
        setConnection("끊김", "waiting");
        return;
      }

      const payload = await response.json();
      latestHostState = isHost ? payload : null;
      const classroom = isHost ? payload.classroom : payload;
      $("gate").hidden = true;
      render(classroom, payload);
    } catch {
      setConnection("연결 중", "waiting");
      setText($("syncStatus"), "연결 끊김");
    } finally {
      requestRunning = false;
    }
  }

  function render(classroom, payload) {
    const files = Array.isArray(classroom.files) ? classroom.files : [];

    // 보던 파일이 사라졌으면 말없이 갈아치우지 않고 알려준다.
    if (selectedId && !files.some((file) => file.id === selectedId)) {
      if (selectedName) notify(`${selectedName} 공유가 끝나 다른 파일로 옮겼어요`);
      selectedId = classroom.professorActiveId || files[0]?.id || "";
    } else if (!selectedId) {
      selectedId = classroom.professorActiveId || files[0]?.id || "";
    }

    const selected = files.find((file) => file.id === selectedId);
    const professor = files.find((file) => file.id === classroom.professorActiveId);
    selectedName = selected?.name || "";

    const live = classroom.broadcasting;
    setText($("className"), classroom.className);
    setText($("viewerCount"), String(classroom.viewers));
    setText($("fileCount"), String(files.length));
    setText($("mobileFileCount"), String(files.length));

    const professorName = classroom.professorActiveName || professor?.name || (live ? "없음" : "멈춤");
    setText($("professorFile"), professorName);
    setTitle($("professorFile"), professor?.path || professorName);

    setText($("syncStatus"), live
      ? "실시간"
      : isHost ? "멈춤 · 학생은 마지막 화면을 봐요" : "멈춤 · 마지막 화면");
    setConnection(live ? "실시간" : "멈춤", live ? "live" : "paused");

    if (selected) {
      setText($("fileName"), selected.name);
      setTitle($("fileName"), selected.name);
      setText($("filePath"), selected.path);
      setTitle($("filePath"), selected.path);
      setText($("fileType"), shortLanguage(selected.language));
      setText($("language"), selected.language);
      renderCode(selected);
      $("emptyState").hidden = true;
      $("codeScroll").hidden = false;
    } else {
      setText($("fileName"), "파일 없음");
      setTitle($("fileName"), "파일 없음");
      setText($("filePath"), "교수님이 공유하면 여기에 나타나요.");
      setTitle($("filePath"), "");
      setText($("fileType"), "···");
      setText($("language"), "Text");
      setText($("lineCount"), "0줄");
      $("codeLines").replaceChildren();
      renderedRows = [];
      renderedFileId = "";
      currentContent = "";
      $("emptyState").hidden = false;
      $("codeScroll").hidden = true;
    }

    applyProfessorLine(classroom, selected);
    renderFiles(files, classroom.professorActiveId);
    if (isHost) renderHost(payload);
  }

  // 교수가 보고 있는 줄을 표시하고, 따라가기가 켜져 있으면 그 줄로 스크롤한다.
  function applyProfessorLine(classroom, selected) {
    const line = Number(classroom.professorActiveLine) || 0;
    const onSameFile = Boolean(selected) && selected.id === classroom.professorActiveId;
    const canFollow = onSameFile && line > 0 && line <= renderedRows.length;

    for (const row of document.querySelectorAll(".code-line.is-professor-line"))
      row.classList.remove("is-professor-line");
    if (canFollow) renderedRows[line - 1]?.node.classList.add("is-professor-line");

    const button = $("followProfessor");
    button.hidden = !canFollow;
    if (!canFollow) {
      if (following) setFollowing(false);
      return;
    }

    setText(button, following ? "따라가는 중" : "따라가기");
    setTitle(button, following ? "따라가기 끄기" : `교수님이 보는 ${line}줄로 이동하고 계속 따라갑니다`);
    if (following && line !== followedLine) {
      followedLine = line;
      scrollToLine(line);
    }
  }

  // 줄 단위로 비교해서 바뀐 줄만 다시 그린다. 파일 전체를 다시 쓰면
  // 학생이 드래그한 선택과 키보드 포커스가 폴링할 때마다 사라진다.
  function renderCode(file) {
    const container = $("codeLines");
    const lines = file.content.split("\n");
    const highlighter = highlighterFor(file.language, lines.length);
    currentContent = file.content;

    if (file.id !== renderedFileId) {
      container.replaceChildren();
      renderedRows = [];
      renderedFileId = file.id;
      $("codeScroll").scrollTo({ top: 0, behavior: "auto" });
    }

    let blockState = false;
    for (let index = 0; index < lines.length; index += 1) {
      const text = lines[index];
      const startState = blockState;
      const result = highlighter(text, startState);
      blockState = result.endState;

      const previous = renderedRows[index];
      if (previous && previous.text === text && previous.startState === startState) continue;

      const row = previous ? previous.node : createRow();
      fillRow(row, index, result.tokens);
      if (!previous) container.append(row);
      renderedRows[index] = { node: row, text, startState };
    }

    while (renderedRows.length > lines.length) renderedRows.pop().node.remove();
    setText($("lineCount"), `${lines.length}줄`);
  }

  function createRow() {
    const row = document.createElement("div");
    row.className = "code-line";
    const number = document.createElement("span");
    number.className = "ln";
    number.setAttribute("aria-hidden", "true");
    const code = document.createElement("code");
    code.className = "lc";
    row.append(number, code);
    return row;
  }

  function fillRow(row, index, tokens) {
    const [number, code] = row.children;
    setText(number, String(index + 1));

    if (tokens.length === 0) {
      code.textContent = "";
      return;
    }
    if (tokens.length === 1 && tokens[0].kind === "plain") {
      code.textContent = tokens[0].text;
      return;
    }

    const fragment = document.createDocumentFragment();
    for (const token of tokens) {
      if (token.kind === "plain") {
        fragment.append(document.createTextNode(token.text));
      } else {
        const span = document.createElement("span");
        span.className = `t-${token.kind}`;
        span.textContent = token.text;
        fragment.append(span);
      }
    }
    code.replaceChildren(fragment);
  }

  // --- 구문 강조 -------------------------------------------------------
  // 의존성을 두지 않으려고 직접 훑는다. 완벽한 파서가 아니라 읽기 편하게 만드는 정도다.
  const KEYWORDS = new Set(`
abstract and as async await base bool break byte case catch char class const constexpr continue
decimal def default delegate do double elif else enum event except explicit export extends extern
false final finally fixed float for foreach from function global goto if implements implicit import
in include init instanceof int interface internal is lambda let lock long namespace new nonlocal not
null nullptr object operator or out override params partial pass private protected public raise
readonly record ref return sbyte sealed self short sizeof static string struct switch template this
throw true try typedef typename typeof uint ulong unsafe ushort using var virtual void volatile
while with yield None True False
`.trim().split(/\s+/));

  const C_LIKE = new Set(["C#", "C++", "JavaScript", "TypeScript", "Java", "CSS", "SQL"]);
  const HASH_COMMENT = new Set(["Python", "YAML"]);

  function highlighterFor(language, lineCount) {
    // 아주 큰 파일에서는 강조를 생략한다. 읽는 속도보다 렌더링이 느려지면 손해다.
    if (lineCount > 3000) return (text) => ({ tokens: [{ kind: "plain", text }], endState: false });

    const lineComment = HASH_COMMENT.has(language) ? "#" : "//";
    const blockComments = C_LIKE.has(language);
    const keywords = language !== "HTML" && language !== "XML" && language !== "JSON" && language !== "Text";
    return (text, startState) => tokenize(text, startState, lineComment, blockComments, keywords);
  }

  function tokenize(text, insideBlock, lineComment, blockComments, keywords) {
    const tokens = [];
    let plain = "";
    let index = 0;

    const flush = () => { if (plain) { tokens.push({ kind: "plain", text: plain }); plain = ""; } };
    const push = (kind, value) => { flush(); tokens.push({ kind, text: value }); };

    if (insideBlock) {
      const end = text.indexOf("*/");
      if (end === -1) return { tokens: [{ kind: "comment", text }], endState: true };
      tokens.push({ kind: "comment", text: text.slice(0, end + 2) });
      index = end + 2;
    }

    while (index < text.length) {
      const rest = text.slice(index);

      if (blockComments && rest.startsWith("/*")) {
        const end = rest.indexOf("*/", 2);
        if (end === -1) {
          push("comment", rest);
          return { tokens, endState: true };
        }
        push("comment", rest.slice(0, end + 2));
        index += end + 2;
        continue;
      }

      if (rest.startsWith(lineComment)) {
        push("comment", rest);
        break;
      }

      const quote = text[index];
      if (quote === '"' || quote === "'" || quote === "`") {
        let cursor = index + 1;
        while (cursor < text.length) {
          if (text[cursor] === "\\") { cursor += 2; continue; }
          if (text[cursor] === quote) { cursor += 1; break; }
          cursor += 1;
        }
        push("string", text.slice(index, Math.min(cursor, text.length)));
        index = cursor;
        continue;
      }

      if (/[0-9]/.test(quote) && !/[\w.]/.test(text[index - 1] || "")) {
        const match = /^[0-9][\w.]*/.exec(rest);
        push("number", match[0]);
        index += match[0].length;
        continue;
      }

      if (/[A-Za-z_$#@]/.test(quote)) {
        const match = /^[A-Za-z_$#@][\w$]*/.exec(rest);
        if (keywords && KEYWORDS.has(match[0])) push("keyword", match[0]);
        else plain += match[0];
        index += match[0].length;
        continue;
      }

      plain += quote;
      index += 1;
    }

    flush();
    return { tokens, endState: false };
  }
  // ---------------------------------------------------------------------

  // 목록을 통째로 다시 만들지 않고 id 기준으로 맞춰 넣는다.
  // 그래야 폴링할 때마다 포커스와 선택이 날아가지 않는다.
  function renderFiles(files, professorActiveId) {
    const list = $("fileList");
    const leftover = new Map();
    for (const node of Array.from(list.children)) leftover.set(node.dataset.fileId, node);

    files.forEach((file, index) => {
      let item = leftover.get(file.id);
      if (item) leftover.delete(file.id);
      else item = createFileItem(file);
      updateFileItem(item, file, professorActiveId);
      if (list.children[index] !== item) list.insertBefore(item, list.children[index] ?? null);
    });

    for (const stale of leftover.values()) stale.remove();
  }

  function createFileItem(file) {
    const item = document.createElement("div");
    item.dataset.fileId = file.id;

    const open = document.createElement("button");
    open.type = "button";
    open.className = "file-open";
    open.addEventListener("click", () => {
      selectedId = file.id;
      localStorage.setItem("classroom-live:selected-file", selectedId);
      setFollowing(false);
      openFiles(false);
      // 직접 골랐다는 건 안내를 이해했다는 뜻이다.
      dismissNote();
      void refresh();
    });

    const icon = document.createElement("span");
    icon.className = "file-icon";
    const copy = document.createElement("span");
    copy.className = "file-copy";
    const name = document.createElement("strong");
    const updated = document.createElement("small");
    copy.append(name, updated);
    const badge = document.createElement("span");
    badge.className = "professor-badge";
    badge.textContent = "교수님";
    badge.hidden = true;
    open.append(icon, copy, badge);
    item.append(open);

    let remove = null;
    let hide = null;
    if (isHost) {
      hide = document.createElement("button");
      hide.type = "button";
      hide.className = "hide-file";
      hide.addEventListener("click", async (event) => {
        event.stopPropagation();
        await setHidden(file.id, hide.dataset.hidden !== "1");
      });
      item.append(hide);

      remove = document.createElement("button");
      remove.type = "button";
      remove.className = "remove-file";
      remove.textContent = "×";
      remove.addEventListener("click", async (event) => {
        event.stopPropagation();
        await fetch(`/api/host/files/${encodeURIComponent(file.id)}`, {
          method: "DELETE", headers: { "X-Admin-Token": adminToken },
        });
        await refresh();
      });
      item.append(remove);
    }

    item.parts = { open, icon, name, updated, badge, remove, hide };
    return item;
  }

  function updateFileItem(item, file, professorActiveId) {
    const { open, icon, name, updated, badge, remove, hide } = item.parts;
    const isSelected = file.id === selectedId;

    const className = `file-item${isSelected ? " is-selected" : ""}${file.hidden ? " is-hidden-file" : ""}`;
    if (item.className !== className) item.className = className;
    if (isSelected) open.setAttribute("aria-current", "page");
    else open.removeAttribute("aria-current");

    setTitle(open, `${file.path} · ${file.language}`);
    setText(icon, shortLanguage(file.language));
    setText(name, file.name);
    setText(updated, relativeTime(file.updatedAt));
    badge.hidden = file.id !== professorActiveId;
    if (remove) remove.setAttribute("aria-label", `${file.name} 공유 해제`);
    if (hide) {
      hide.dataset.hidden = file.hidden ? "1" : "0";
      setText(hide, file.hidden ? "보임" : "숨김");
      hide.setAttribute("aria-label",
        file.hidden ? `${file.name} 다시 보이기` : `${file.name} 학생 화면에서 숨기기`);
      hide.classList.toggle("is-active", Boolean(file.hidden));
    }
  }

  function renderHost(payload) {
    setText($("pinValue"), payload.pin);
    setText($("toggleBroadcast"), payload.broadcasting ? "멈춤" : "시작");
    setText($("hostStatus"), payload.visualStudioStatus);
    setTitle($("hostStatus"), payload.visualStudioStatus);

    // Visual Studio로 돌아가지 않아도 여기서 공유와 숨김을 켜고 끌 수 있다.
    const share = $("toggleShare");
    const hide = $("toggleHide");
    const current = payload.currentFileName;
    share.hidden = !current;
    hide.hidden = !current || !payload.currentFileShared;
    if (!current) return;

    const shared = Boolean(payload.currentFileShared);
    const shareable = Boolean(payload.currentFileShareable);
    setText(share, shared ? `${current} 공유 해제` : `${current} 공유`);
    setTitle(share, shared
      ? "학생 목록에서 뺍니다"
      : shareable ? "학생에게 이 파일을 보여줍니다" : "공유할 수 없는 타입의 파일입니다");
    share.classList.toggle("is-active", shared);
    share.classList.toggle("is-blocked", !shared && !shareable);
    share.dataset.shared = shared ? "1" : "0";
    share.dataset.shareable = shareable ? "1" : "0";

    const hidden = Boolean(payload.currentFileHidden);
    setText(hide, hidden ? "다시 보이기" : "숨김");
    setTitle(hide, hidden
      ? "학생 화면에 다시 보이게 합니다"
      : "목록에는 두고 학생 화면에서만 감춥니다");
    hide.classList.toggle("is-active", hidden);
    hide.dataset.hidden = hidden ? "1" : "0";
    hide.dataset.fileId = currentFileId(payload);
  }

  // 현재 파일의 id는 목록에서 이름으로 찾는다. 교수 화면은 숨긴 파일까지 받는다.
  function currentFileId(payload) {
    const files = payload.classroom?.files ?? [];
    return files.find((file) => file.name === payload.currentFileName)?.id ?? "";
  }

  function setConnection(text, kind) {
    const element = $("connection");
    const className = `connection is-${kind}`;
    if (element.className !== className) element.className = className;
    setText(element.querySelector("b"), text);
  }

  function shortLanguage(language) {
    return ({ "C#": "C#", "C++": "C++", JavaScript: "JS", TypeScript: "TS", Python: "PY" })[language] || language.slice(0, 3).toUpperCase();
  }

  function relativeTime(value) {
    const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
    if (seconds < 10) return "방금 전";
    if (seconds < 60) return `${seconds}초 전`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}분 전`;
    return `${Math.floor(seconds / 3600)}시간 전`;
  }

  void refresh();
  setInterval(refresh, 750);
})();
