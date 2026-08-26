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
  const studentLanguageKey = "classroom-live:language";
  let catalog = Object.create(null);
  let localeCode = "en";
  let localeOptions = [];
  let hostLanguage = "en";
  let pin = sessionStorage.getItem("classroom-live:pin") || "";
  let selectedId = localStorage.getItem("classroom-live:selected-file") || "";
  let selectedName = "";
  let latestHostState = null;
  let latestClassroom = null;
  // 계층 상태에는 원문을 넣지 않는다. 선택한 파일만 revision별로 메모리에 보관한다.
  const contentCache = new Map();
  const collapsedWorkspaces = loadStringSet("classroom-live:collapsed-workspaces");
  const collapsedProjects = loadStringSet("classroom-live:collapsed-projects");
  const collapsedFolders = loadStringSet("classroom-live:collapsed-folders");
  let requestRunning = false;
  let endListenerRunning = false;
  let blockedUntil = 0;
  let shuttingDown = false;
  let currentSessionState = "connecting";

  const SESSION_STATES = Object.freeze({
    connecting: { key: "connecting", kind: "waiting", viewers: false },
    before: { key: "before", kind: "waiting", viewers: true },
    live: { key: "live", kind: "live", viewers: true },
    paused: { key: "paused", kind: "paused", viewers: true },
    ended: { key: "ended", kind: "ended", viewers: false },
    disconnected: { key: "disconnected", kind: "waiting", viewers: false },
  });

  const FONT_STEPS = [11, 12.5, 14, 16, 18, 21, 24];
  const DEFAULT_FONT_INDEX = 1;
  // Number(null)은 0이다. 그대로 넘기면 저장값이 없을 때 가장 작은 단계로 시작한다.
  const storedFont = localStorage.getItem("classroom-live:font");
  let fontIndex = clampFontIndex(storedFont === null ? DEFAULT_FONT_INDEX : Number(storedFont));
  let wrapEnabled = localStorage.getItem("classroom-live:wrap") === "1";
  let following = false;
  let followedLine = 0;
  let followScrollTimer = 0;
  let selectedLineAnchor = 0;
  let selectedLineStart = 0;
  let selectedLineEnd = 0;
  let paintedLineStart = 0;
  let paintedLineEnd = 0;
  let lineDragPointer = null;
  let lineDragLast = 0;
  let lineDragTouch = false;
  let lineDragGutterX = 0;
  let lineDragStartY = 0;
  let lineDragClientY = 0;
  let lineDragMoved = false;
  let lineDragAutoFrame = 0;
  let pendingCodeTouch = null;
  let lineScrollPointer = null;
  let lineScrollLastX = 0;
  let lineScrollLastY = 0;
  let lineScrollClientY = 0;
  let lineDragPrimaryRegion = "";
  let lineDragTouchIdentifier = null;
  let codeHoldTimer = 0;

  // 화면에 그려진 줄. { node, text, startState } 로 줄 단위 비교를 한다.
  let renderedRows = [];
  let renderedFileId = "";
  let renderedRevision = -1;
  let currentContent = "";
  let noticeTimer = 0;
  let confirmResolve = null;
  let confirmClosing = false;
  let restoreClosing = false;
  let restorePromptBusy = false;

  if (isHost) $("hostControls").hidden = false;
  if (!isHost && !pin) showGate("");

  function t(key, values = {}) {
    return String(catalog[key] ?? key).replace(/\{([a-z][a-z0-9]*)\}/gi,
      (_, name) => values[name] ?? `{${name}}`);
  }

  function plural(key, count) {
    const form = new Intl.PluralRules(localeCode).select(count);
    const candidate = `${key}.${form}`;
    return t(catalog[candidate] ? candidate : `${key}.other`, { count });
  }

  function applyStaticTranslations() {
    document.documentElement.lang = localeCode;
    document.documentElement.dir = catalog.$direction === "rtl" ? "rtl" : "ltr";
    for (const element of document.querySelectorAll("[data-i18n]"))
      setText(element, t(element.dataset.i18n));
    for (const element of document.querySelectorAll("[data-i18n-title]"))
      setTitle(element, t(element.dataset.i18nTitle));
    for (const element of document.querySelectorAll("[data-i18n-aria]"))
      element.setAttribute("aria-label", t(element.dataset.i18nAria));
    setText($("languageCode"), localeCode.toUpperCase());
    setTitle($("languageButton"), t("language.choose"));
    $("languageButton").setAttribute("aria-label", t("language.choose"));
  }

  async function loadLocale(code) {
    const safe = localeOptions.some((item) => item.code === code) ? code : "en";
    const response = await fetch(`/locales/${encodeURIComponent(safe)}.json`, { cache: "no-store" });
    if (!response.ok) throw new Error("locale");
    catalog = await response.json();
    localeCode = safe;
    applyStaticTranslations();
    renderLanguageOptions();
    setSessionState(currentSessionState || "connecting");
    if (latestHostState) render(latestHostState.classroom, latestHostState);
  }

  async function initializeLocalization() {
    try {
      const response = await fetch("/api/locales", { cache: "no-store" });
      const data = await response.json();
      localeOptions = data.locales || [];
      hostLanguage = data.language || "en";
      const chosen = isHost ? hostLanguage : localStorage.getItem(studentLanguageKey) || hostLanguage;
      await loadLocale(chosen);
    } catch {
      localeOptions = [{ code: "ko", name: "한국어", direction: "ltr" }];
      catalog = Object.fromEntries([...document.querySelectorAll("[data-i18n]")]
        .map((element) => [element.dataset.i18n, element.textContent]));
      applyStaticTranslations();
      setSessionState("connecting");
    }
  }

  function renderLanguageOptions() {
    const list = $("languageList");
    if (!list) return;
    list.replaceChildren(...localeOptions.map((item) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `language-option${item.code === localeCode ? " is-selected" : ""}`;
      button.textContent = item.name;
      button.addEventListener("click", () => void chooseLanguage(item.code));
      return button;
    }));
  }

  async function chooseLanguage(code) {
    await loadLocale(code);
    if (isHost) {
      await fetch("/api/host/language", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-Admin-Token": adminToken },
        body: JSON.stringify({ code }),
      });
      hostLanguage = code;
    } else {
      localStorage.setItem(studentLanguageKey, code);
    }
  }

  $("languageButton").addEventListener("click", () => {
    renderLanguageOptions();
    $("languageDialog").showModal();
  });
  $("languageForm").addEventListener("submit", (event) => {
    event.preventDefault();
    $("languageDialog").close();
  });

  // 한 번 읽으면 끝인 안내다. 교수 화면에는 애초에 필요 없고,
  // 학생도 닫거나 파일을 직접 골라보면 다시 뜨지 않는다.
  if (!isHost && localStorage.getItem("classroom-live:note-read") !== "1") showFollowNote();
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

  addEventListener("pagehide", () => {
    if (isHost || !pin || shuttingDown) return;
    void fetch("/api/viewer/leave", {
      method: "POST",
      headers: { "X-Classroom-Pin": pin },
      keepalive: true,
    }).catch(() => { /* 90초 만료가 대신 정리한다. */ });
  });

  $("mobileFiles").addEventListener("click", () => {
    if ($("filePanel").classList.contains("is-open")) closeFiles();
    else setFilesOpen(true, true);
  });
  $("backdrop").addEventListener("click", closeFiles);
  addEventListener("popstate", (event) => {
    setFilesOpen(event.state?.classroomFiles === true);
    if ($("confirmDialog").open && event.state?.classroomConfirm !== true)
      closeConfirm("cancel", false);
    if ($("restoreDialog").open && event.state?.classroomRestore !== true)
      closeRestore(false, false);
  });
  addEventListener("keydown", (event) => {
    if (event.key === "Escape" && $("filePanel").classList.contains("is-open")) {
      event.preventDefault();
      closeFiles();
    }
  });
  $("confirmForm").addEventListener("submit", (event) => {
    event.preventDefault();
    closeConfirm(event.submitter?.value || "cancel");
  });
  $("confirmDialog").addEventListener("cancel", (event) => {
    event.preventDefault();
    closeConfirm("cancel");
  });
  $("restoreForm").addEventListener("submit", (event) => {
    event.preventDefault();
    closeRestore(event.submitter?.value === "restore");
  });
  $("restoreDialog").addEventListener("cancel", (event) => {
    event.preventDefault();
    closeRestore(false);
  });

  $("toggleWrap").addEventListener("click", () => {
    wrapEnabled = !wrapEnabled;
    localStorage.setItem("classroom-live:wrap", wrapEnabled ? "1" : "0");
    applyWrap();
  });
  $("fontSmaller").addEventListener("click", () => stepFont(-1));
  $("fontLarger").addEventListener("click", () => stepFont(1));
  $("copyCode").addEventListener("click", async () => {
    if (!currentContent) return notify(t("notice.noCode"));
    notify(await copyText(currentContent)
      ? t("notice.codeCopied")
      : t("notice.copyFailed"));
  });
  $("followProfessor").addEventListener("click", () => setFollowing(!following));
  $("followFile").addEventListener("click", jumpToProfessorFile);
  $("codeScroll").addEventListener("scroll", () => {
    if (lineDragPointer !== null) {
      if (lineDragTouch) updateTouchLineSelection();
      return;
    }
    clearTimeout(followScrollTimer);
    followScrollTimer = setTimeout(stopFollowingOutsideProfessorLine, 120);
  });

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
    const urls = latestHostState?.studentUrls ?? [];
    if (urls.length === 0) return;
    // 주소가 여러 개면 첫 번째를 말없이 복사하면 안 된다. 가상 어댑터나
    // 다른 네트워크 주소를 학생에게 나눠주면 아무도 못 붙는데 이유를 알 수 없다.
    if (urls.length > 1) {
      popup(t("notice.chooseAddress"), urls.map((url) => ({
        label: hostOf(url),
        select: () => void copyAndFlash(url),
      })));
      return;
    }
    await copyAndFlash(urls[0]);
  });
  $("toggleShare").addEventListener("click", async () => {
    const button = $("toggleShare");
    if (button.dataset.shared === "1") return;
    // 확장자, 크기, 솔루션 밖 여부는 호스트의 보안 규칙이 정한다.
    if (button.dataset.shareable !== "1")
      return notify(button.dataset.reason || t("notice.notShareable"));
    button.disabled = true;
    try {
      const response = await fetch("/api/host/share", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-Admin-Token": adminToken },
        body: JSON.stringify({ enabled: true }),
      });
      notify(response.ok
        ? (latestClassroom?.everStarted ? t("notice.shareRequested") : t("notice.prepareRequested"))
        : t("notice.requestFailed"));
    } catch {
      notify(t("notice.extensionOffline"));
    } finally {
      button.disabled = false;
    }
    await refresh();
  });

  $("copyPin").addEventListener("click", async () => {
    const pinValue = $("pinValue").textContent.trim();
    if (!pinValue || pinValue.startsWith("-")) return;
    notify(await copyText(pinValue) ? t("notice.pinCopied") : t("notice.copyGenericFailed"));
  });

  $("shutdown").addEventListener("click", async () => {
    const student = latestHostState?.classroom?.viewers ?? 0;
    if (!await confirmShutdown(student)) return;

    shuttingDown = true;
    try {
      await fetch("/api/host/shutdown", { method: "POST", headers: { "X-Admin-Token": adminToken } });
    } catch { /* 종료 중에 연결이 끊기는 것은 정상이다. */ }
    setSessionState("ended");
    setText($("hostStatus"), t("state.ended.connection"));
    // 서버가 없으므로 이 버튼들은 이제 아무것도 하지 못한다.
    // 화면이 덮이더라도 눌리는 상태로 두지 않는다.
    for (const id of ["toggleBroadcast", "shutdown", "allowFirewall", "copyLink", "toggleShare", "copyPin"])
      $(id).disabled = true;
    $("notice").hidden = true;
    showEndedScene();
  });

  // 종료 화면은 되돌아갈 수 없는 상태다. 토스트로 스쳐 지나가면 놓치기 쉬워
  // 화면을 통째로 덮는다.
  function showEndedScene() {
    const scene = $("ended");
    scene.hidden = false;
    // display:none 에서 바로 클래스를 붙이면 전환이 일어나지 않는다.
    // 레이아웃이 한 번 잡힌 다음 프레임에 붙여야 처음부터 부드럽게 떠오른다.
    requestAnimationFrame(() => requestAnimationFrame(() => scene.classList.add("is-visible")));
  }

  $("allowFirewall").addEventListener("click", async () => {
    const button = $("allowFirewall");
    button.disabled = true;
    button.textContent = t("host.firewall.requesting");
    try {
      const response = await fetch("/api/host/firewall", {
        method: "POST", headers: { "X-Admin-Token": adminToken },
      });
      button.textContent = response.ok ? t("host.firewall.allowed") : t("host.firewall.retry");
    } catch {
      button.textContent = t("host.firewall.retry");
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
      const preparing = latestClassroom?.everStarted !== true;
      notify(response.ok
        ? (preparing
          ? (hidden ? t("notice.preparedHidden") : t("notice.preparedVisible"))
          : (hidden ? t("notice.hidden") : t("notice.visible")))
        : t("notice.requestFailed"));
    } catch {
      notify(t("notice.requestFailed"));
    }
    await refresh();
  }

  /// 주소에서 학생에게 불러줄 부분만 뽑는다. PIN까지 읽어줄 필요는 없다.
  function hostOf(url) {
    try { return new URL(url).host; } catch { return url; }
  }

  async function copyAndFlash(url) {
    const button = $("copyLink");
    const copied = await copyText(url);
    button.textContent = copied ? t("notice.copySuccessShort") : t("notice.copyFailureShort");
    notify(copied ? t("notice.addressCopied") : t("notice.copyGenericFailed"));
    setTimeout(() => { button.textContent = t("host.copyAddress"); }, 1200);
  }

  function showFollowNote() {
    const note = $("followNote");
    note.hidden = false;
    requestAnimationFrame(() => requestAnimationFrame(() => note.classList.add("is-visible")));
  }

  function dismissNote() {
    const note = $("followNote");
    localStorage.setItem("classroom-live:note-read", "1");
    if (note.hidden || note.classList.contains("is-closing")) return;
    note.classList.add("is-closing");
    const delay = matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 160;
    setTimeout(() => {
      note.hidden = true;
      note.classList.remove("is-visible", "is-closing");
    }, delay);
  }

  function setFilesOpen(open, addHistory = false) {
    $("filePanel").classList.toggle("is-open", open);
    $("backdrop").hidden = !open;
    $("mobileFiles").setAttribute("aria-expanded", String(open));
    $("mobileFiles").setAttribute("aria-label", t(open ? "mobile.files.close" : "mobile.files.open"));
    if (open && addHistory && history.state?.classroomFiles !== true)
      history.pushState({ ...history.state, classroomFiles: true }, "");
  }

  function closeFiles() {
    if (history.state?.classroomFiles === true) history.back();
    else setFilesOpen(false);
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

  function confirmShutdown(viewers) {
    const dialog = $("confirmDialog");
    const message = $("confirmMessage");
    message.textContent = viewers > 0 ? plural("dialog.end.viewers", viewers) : "";
    message.hidden = viewers === 0;
    dialog.returnValue = "cancel";
    history.pushState({ ...history.state, classroomConfirm: true }, "");
    dialog.showModal();
    return new Promise((resolve) => { confirmResolve = resolve; });
  }

  function closeConfirm(value, popHistory = true) {
    const dialog = $("confirmDialog");
    if (!dialog.open || confirmClosing) return;
    confirmClosing = true;
    dialog.classList.add("is-closing");
    const delay = matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 160;
    setTimeout(() => {
      dialog.classList.remove("is-closing");
      dialog.close(value);
      confirmClosing = false;
      if (popHistory && history.state?.classroomConfirm === true) history.back();
      const resolve = confirmResolve;
      confirmResolve = null;
      resolve?.(value === "confirm");
    }, delay);
  }

  function showRestore(count) {
    if (restorePromptBusy || $("restoreDialog").open) return;
    setText($("restoreMessage"), plural("dialog.restore.message", count));
    history.pushState({ ...history.state, classroomRestore: true }, "");
    $("restoreDialog").showModal();
  }

  function closeRestore(restore, popHistory = true) {
    const dialog = $("restoreDialog");
    if (!dialog.open || restoreClosing) return;
    restoreClosing = true;
    restorePromptBusy = true;
    dialog.classList.add("is-closing");
    const delay = matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 160;
    setTimeout(async () => {
      dialog.classList.remove("is-closing");
      dialog.close(restore ? "restore" : "skip");
      restoreClosing = false;
      if (popHistory && history.state?.classroomRestore === true) history.back();
      try {
        await fetch("/api/host/restore", {
          method: "POST",
          headers: { "Content-Type": "application/json", "X-Admin-Token": adminToken },
          body: JSON.stringify({ enabled: restore }),
        });
      } finally {
        restorePromptBusy = false;
        await refresh();
      }
    }, delay);
  }

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
    $("followControls").classList.toggle("is-active", following);
    followedLine = 0;
    if (latestClassroom)
      applyProfessorLine(latestClassroom,
        latestClassroom.files?.find((file) => file.id === selectedId));
    // OFF에서 켠 순간만 교수 파일까지 맞춘다. 이후 파일 이동은 ▶▶가 담당한다.
    if (following) jumpToProfessorFile();
  }

  function jumpToProfessorFile() {
    const classroom = latestClassroom;
    const professor = classroom?.files?.find((file) =>
      file.id === classroom.professorActiveId && !file.pending && !file.missing);
    if (!classroom || !following || classroom.professorAway || !professor || professor.id === selectedId) return;

    selectedId = professor.id;
    localStorage.setItem("classroom-live:selected-file", selectedId);
    followedLine = 0;
    closeFiles();
    dismissNote();
    render(classroom, latestHostState);
    void refresh();
  }

  function scrollToLine(line) {
    const row = renderedRows[line - 1]?.node;
    if (!row) return;
    const scroller = $("codeScroll");
    const target = row.offsetTop - scroller.clientHeight / 3;
    const smooth = !matchMedia("(prefers-reduced-motion: reduce)").matches;
    scroller.scrollTo({ top: Math.max(0, target), behavior: smooth ? "smooth" : "auto" });
  }

  // 스크롤 입력 종류를 추측하지 않는다. 스크롤이 멈춘 뒤 교수 줄이 완전히 사라졌을 때만
  // 학생이 화면 제어를 가져간 것으로 본다. 코드 선택과 작은 위치 조정은 따라가기를 유지한다.
  function stopFollowingOutsideProfessorLine() {
    if (!following || !latestClassroom || latestClassroom.professorAway ||
        selectedId !== latestClassroom.professorActiveId) return;
    const line = Number(latestClassroom.professorActiveLine) || 0;
    const row = renderedRows[line - 1]?.node;
    if (!row) return;

    const scroller = $("codeScroll");
    const top = row.offsetTop;
    const bottom = top + row.offsetHeight;
    const viewTop = scroller.scrollTop;
    const viewBottom = viewTop + scroller.clientHeight;
    if (!overlapsViewport(top, bottom, viewTop, viewBottom)) setFollowing(false);
  }

  function overlapsViewport(top, bottom, viewTop, viewBottom) {
    return bottom > viewTop && top < viewBottom;
  }

  async function refresh() {
    if (requestRunning || shuttingDown) return;
    // 틀린 PIN을 들고 0.75초마다 계속 두드리면 서버의 시도 제한에 스스로 걸린다.
    if (!isHost && !pin) return;
    if (Date.now() < blockedUntil) return;

    requestRunning = true;
    const requestedFileId = selectedId;
    let refreshSelection = false;
    try {
      const response = await fetch(stateUrl(isHost ? "/api/host/state" : "/api/state"), {
        cache: "no-store",
        headers: isHost
          ? { "X-Admin-Token": adminToken }
          : { "X-Classroom-Pin": pin },
      });

      if (!response.ok) {
        const rateLimit = !isHost ? studentRateLimit(response) : "";
        if (rateLimit === "pin-locked") {
          blockedUntil = Date.now() + 60_000;
          showGate(t("join.tooMany"));
        } else if (rateLimit === "retry") {
          return;
        } else if (!isHost && response.status === 401) {
          pin = "";
          sessionStorage.removeItem("classroom-live:pin");
          showGate(t("join.badPin"));
        }
        setSessionState("disconnected");
        return;
      }

      const payload = await response.json();
      latestHostState = isHost ? payload : null;
      const classroom = isHost ? payload.classroom : payload;
      mergeFileContents(classroom);
      latestClassroom = classroom;
      hostLanguage = classroom.language || payload.language || hostLanguage;
      if (!isHost && !localStorage.getItem(studentLanguageKey) && hostLanguage !== localeCode)
        await loadLocale(hostLanguage);
      $("gate").hidden = true;
      render(classroom, payload);
      // 첫 접속이나 요청 도중 파일을 바꾼 경우, 다음 0.75초를 기다리지 않는다.
      refreshSelection = Boolean(selectedId) && selectedId !== requestedFileId;
      void listenForEnd();
    } catch {
      setSessionState("disconnected");
    } finally {
      requestRunning = false;
      if (refreshSelection && !shuttingDown) void refresh();
    }
  }

  function studentRateLimit(response) {
    if (response.status !== 429) return "";
    return response.headers.get("X-Classroom-Pin-Locked") === "1" ? "pin-locked" : "retry";
  }

  async function listenForEnd() {
    if (endListenerRunning || shuttingDown || (!isHost && !pin)) return;

    const listenerPin = pin;
    endListenerRunning = true;
    try {
      const response = await fetch(stateUrl(isHost ? "/api/host/end" : "/api/end"), {
        cache: "no-store",
        headers: isHost
          ? { "X-Admin-Token": adminToken }
          : { "X-Classroom-Pin": listenerPin },
      });
      // 학생 종료 대기는 서버 자원을 오래 잡지 않도록 60초마다 새로 연결한다.
      if (response.status === 204) return;
      if (!response.ok) return;

      const payload = await response.json();
      const classroom = isHost ? payload.classroom : payload;
      if (!classroom?.ended) return;
      mergeFileContents(classroom);
      latestHostState = isHost ? payload : null;
      latestClassroom = classroom;
      render(classroom, payload);
    } catch {
      // 비정상 종료나 네트워크 단절은 기존 짧은 상태 요청이 "끊김"으로 판정한다.
    } finally {
      endListenerRunning = false;
    }
  }

  function stateUrl(path) {
    if (!selectedId) return path;
    const query = new URLSearchParams({ fileId: selectedId });
    const cached = contentCache.get(selectedId);
    if (cached) query.set("revision", String(cached.revision));
    return `${path}?${query}`;
  }

  function mergeFileContents(classroom) {
    const files = Array.isArray(classroom?.files) ? classroom.files : [];
    const visible = new Set(files.map((file) => file.id));
    for (const file of files) {
      if (Object.prototype.hasOwnProperty.call(file, "content"))
        contentCache.set(file.id, { revision: Number(file.revision), content: file.content ?? "" });
    }
    for (const id of contentCache.keys())
      if (!visible.has(id)) contentCache.delete(id);
  }

  function cachedContent(file) {
    const cached = file ? contentCache.get(file.id) : null;
    return cached && cached.revision === Number(file.revision) ? cached.content : null;
  }

  function render(classroom, payload) {
    const ended = Boolean(classroom.ended);
    if (ended) {
      shuttingDown = true;
      setSessionState("ended");
      if (isHost) {
        showEndedScene();
        return;
      }
    }

    const files = Array.isArray(classroom.files) ? classroom.files : [];
    const readableFiles = files.filter((file) => !file.pending && !file.missing);

    // 보던 파일이 사라졌으면 말없이 갈아치우지 않고 알려준다.
    if (selectedId && !readableFiles.some((file) => file.id === selectedId)) {
      const next = classroom.professorActiveId || readableFiles[0]?.id || "";
      // 옮겨갈 파일이 없는데 "옮겼어요"라고 하면 안 된다.
      if (selectedName) notify(next
        ? t("notice.fileEndedMoved", { name: selectedName })
        : t("notice.fileEnded", { name: selectedName }));
      selectedId = next;
    } else if (!selectedId) {
      selectedId = classroom.professorActiveId || readableFiles[0]?.id || "";
    }

    const selected = readableFiles.find((file) => file.id === selectedId);
    const professor = files.find((file) => file.id === classroom.professorActiveId);
    selectedName = selected?.name || "";

    const live = classroom.broadcasting;
    const started = Boolean(classroom.everStarted);
    if (!ended)
      setSessionState(live ? "live" : started ? "paused" : "before");
    setText($("viewerCount"), String(classroom.viewers));
    setText($("viewerSuffix"), t("viewer.count", { count: "" }));
    setText($("fileCount"), String(files.length));
    setText($("mobileFileCount"), String(files.length));

    const professorName = ended ? t("professor.ended") : classroom.professorActiveName || professor?.name ||
      (classroom.professorAway ? t("professor.away") : live ? t("professor.none") :
        started ? t("professor.paused") : t("professor.before"));
    setText($("professorFile"), professorName);
    setTitle($("professorFile"), professor?.path || professorName);

    if (selected) {
      setText($("fileName"), selected.name);
      setTitle($("fileName"), selected.name);
      setText($("filePath"), selected.path);
      setTitle($("filePath"), selected.path);
      setText($("fileType"), shortLanguage(selected.language));
      setText($("language"), selected.language);
      const content = cachedContent(selected);
      if (content === null) clearCode(selected.id);
      else renderCode({ ...selected, content });
      $("emptyState").hidden = true;
      $("codeScroll").hidden = false;
    } else {
      setText($("fileName"), t("file.none"));
      setTitle($("fileName"), t("file.none"));
      setText($("filePath"), t("file.none.path"));
      setTitle($("filePath"), "");
      setText($("fileType"), "···");
      setText($("language"), "Text");
      setText($("lineCount"), plural("file.lines", 0));
      $("codeLines").replaceChildren();
      renderedRows = [];
      renderedFileId = "";
      renderedRevision = -1;
      currentContent = "";
      $("emptyState").hidden = false;
      $("codeScroll").hidden = true;
    }

    applyProfessorLine(classroom, selected);
    renderFiles(files, classroom.professorActiveId,
      classroom.professorWorkspaceId, classroom.professorProjectId);
    if (isHost) renderHost(payload);
  }

  // 교수가 보고 있는 줄을 표시하고, 따라가기가 켜져 있으면 그 줄로 스크롤한다.
  function applyProfessorLine(classroom, selected) {
    const line = Number(classroom.professorActiveLine) || 0;
    const anchor = Number(classroom.professorAnchorLine) || line;
    const onSameFile = Boolean(selected) && selected.id === classroom.professorActiveId;
    const professor = classroom.files?.find((file) =>
      file.id === classroom.professorActiveId && !file.pending && !file.missing);
    const canFollow = !classroom.ended && !classroom.professorAway && onSameFile &&
      line > 0 && line <= renderedRows.length;
    const canJump = following && !classroom.ended && !classroom.professorAway &&
      Boolean(professor) && !onSameFile;

    for (const row of document.querySelectorAll(".code-line.is-professor-line, .code-line.is-professor-selection"))
      row.classList.remove("is-professor-line", "is-professor-selection");
    if (canFollow) {
      const first = Math.max(1, Math.min(line, anchor));
      const last = Math.min(renderedRows.length, Math.max(line, anchor));
      for (let selectedLine = first; selectedLine <= last; selectedLine += 1)
        renderedRows[selectedLine - 1]?.node.classList.add("is-professor-selection");
    }
    if (canFollow) renderedRows[line - 1]?.node.classList.add("is-professor-line");

    const controls = $("followControls");
    const button = $("followProfessor");
    const jump = $("followFile");
    controls.hidden = classroom.ended;
    jump.disabled = !canJump;
    setTitle(jump, t("follow.jump"));
    jump.setAttribute("aria-label", t("follow.jump"));
    if (controls.hidden) return;

    setText(button, following ? t("action.following") : t("action.follow"));
    setTitle(button, following ? t("follow.off") : t("follow.on", { line }));
    if (following && canFollow && line !== followedLine) {
      followedLine = line;
      scrollToLine(line);
    }
  }

  // 줄 단위로 비교해서 바뀐 줄만 다시 그린다. 파일 전체를 다시 쓰면
  // 학생이 드래그한 선택과 키보드 포커스가 폴링할 때마다 사라진다.
  function renderCode(file) {
    const container = $("codeLines");
    const revision = Number(file.revision);
    if (file.id === renderedFileId && revision === renderedRevision) {
      setText($("lineCount"), plural("file.lines", renderedRows.length));
      return;
    }
    const lines = file.content.split("\n");
    const highlighter = highlighterFor(file.language, lines.length);
    currentContent = file.content;
    renderedRevision = revision;

    if (file.id !== renderedFileId) {
      clearStudentLineSelection();
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
    if (selectedLineAnchor > lines.length) clearStudentLineSelection();
    else if (selectedLineAnchor) {
      selectedLineStart = Math.min(selectedLineStart, lines.length);
      selectedLineEnd = Math.min(selectedLineEnd, lines.length);
      applyStudentLineSelection();
    }
    setText($("lineCount"), plural("file.lines", lines.length));
  }

  function clearCode(fileId) {
    if (fileId === renderedFileId && currentContent === "" && renderedRows.length === 0) return;
    clearStudentLineSelection();
    $("codeLines").replaceChildren();
    renderedRows = [];
    renderedFileId = fileId;
    renderedRevision = -1;
    currentContent = "";
    setText($("lineCount"), plural("file.lines", 0));
    $("codeScroll").scrollTo({ top: 0, behavior: "auto" });
  }

  function createRow() {
    const row = document.createElement("div");
    row.className = "code-line";
    const number = document.createElement("button");
    number.type = "button";
    number.className = "ln";
    number.setAttribute("aria-pressed", "false");
    number.addEventListener("pointerdown", beginStudentLineDrag);
    number.addEventListener("pointermove", continueStudentLineDrag);
    number.addEventListener("pointerup", endStudentLineDrag);
    number.addEventListener("pointercancel", endStudentLineDrag);
    number.addEventListener("lostpointercapture", endStudentLineDrag);
    number.addEventListener("click", (event) => {
      event.preventDefault();
      // 포인터 클릭은 pointerdown에서 이미 처리했다. 키보드 클릭만 여기서 받는다.
      if (event.detail) return;
      selectStudentLines(Number(row.dataset.line), event.shiftKey);
    });
    const code = document.createElement("code");
    code.className = "lc";
    code.addEventListener("pointerdown", beginCodeTouch);
    code.addEventListener("pointermove", continueCodeTouch);
    code.addEventListener("pointerup", endCodeTouch);
    code.addEventListener("pointercancel", endCodeTouch);
    code.addEventListener("lostpointercapture", endCodeTouch);
    code.addEventListener("touchstart", beginCodeHold, { passive: true });
    code.addEventListener("touchmove", continueCodeHold, { passive: false });
    code.addEventListener("touchend", endCodeHold);
    code.addEventListener("touchcancel", endCodeHold);
    code.addEventListener("contextmenu", (event) => {
      if (lineDragPrimaryRegion === "code") event.preventDefault();
    });
    row.append(number, code);
    return row;
  }

  function fillRow(row, index, tokens) {
    const [number, code] = row.children;
    row.dataset.line = String(index + 1);
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

  function lineSelectionRange(anchor, line, extend) {
    const nextAnchor = extend && anchor ? anchor : line;
    return {
      anchor: nextAnchor,
      start: Math.min(nextAnchor, line),
      end: Math.max(nextAnchor, line),
    };
  }

  function beginStudentLineDrag(event) {
    if (event.button !== 0) return;
    if (lineDragPointer !== null) {
      // 코드 영역을 홀딩해 기준점을 잡은 뒤 거터에 새 손가락을 대는 경로다.
      if (event.pointerType === "touch" && lineDragPrimaryRegion === "code") {
        event.preventDefault();
        startTouchScroll(event);
      }
      return;
    }
    event.preventDefault();
    const line = Number(event.currentTarget.parentElement?.dataset.line);
    if (!renderedRows[line - 1]) return;
    clearTimeout(followScrollTimer);

    // 코드 영역을 먼저 누른 뒤 거터에 두 번째 손가락을 댄 경우에도 첫 손가락의
    // 줄을 기준점으로 삼는다. 두 번째 손가락은 아래 startTouchScroll에서 스크롤한다.
    if (event.pointerType === "touch" && pendingCodeTouch) {
      const anchor = pendingCodeTouch;
      pendingCodeTouch = null;
      const gutterRect = event.currentTarget.getBoundingClientRect();
      clearTimeout(codeHoldTimer);
      codeHoldTimer = 0;
      lineDragTouchIdentifier = anchor.touchIdentifier;
      beginTouchLineSelection(anchor.pointerId, anchor.line, anchor.clientY,
        gutterRect.left + gutterRect.width / 2, "code");
      startTouchScroll(event);
      return;
    }

    lineDragPointer = event.pointerId;
    lineDragLast = line;
    lineDragTouch = event.pointerType === "touch";
    const gutterRect = event.currentTarget.getBoundingClientRect();
    lineDragGutterX = gutterRect.left + gutterRect.width / 2;
    lineDragStartY = event.clientY;
    lineDragClientY = event.clientY;
    lineDragPrimaryRegion = lineDragTouch ? "gutter" : "mouse";
    selectStudentLines(line, event.shiftKey);
    if (lineDragTouch) $("codeScroll").classList.add("is-touch-line-selection");
    event.currentTarget.setPointerCapture?.(event.pointerId);
  }

  function continueStudentLineDrag(event) {
    if (event.pointerId === lineScrollPointer) {
      continueTouchScroll(event);
      return;
    }
    if (event.pointerId !== lineDragPointer) return;
    if (lineDragTouch) {
      lineDragClientY = event.clientY;
      if (Math.abs(lineDragClientY - lineDragStartY) >= 6) lineDragMoved = true;
      updateTouchLineSelection();
      scheduleTouchLineAutoScroll();
      return;
    }
    const gutter = document.elementFromPoint(event.clientX, event.clientY)?.closest(".ln");
    if (!gutter || !$("codeLines").contains(gutter)) return;
    const line = Number(gutter.parentElement?.dataset.line);
    if (line === lineDragLast || !renderedRows[line - 1]) return;
    lineDragLast = line;
    selectStudentLines(line, true);
  }

  function endStudentLineDrag(event) {
    const action = touchPointerEndAction(event.pointerId, lineDragPointer, lineScrollPointer);
    if (action === "release-scroll") {
      stopTouchScroll();
      return;
    }
    if (action === "finish") finishStudentLineDrag();
  }

  function finishStudentLineDrag() {
    const commitTouchSelection = lineDragTouch;
    cancelAnimationFrame(lineDragAutoFrame);
    $("codeScroll").classList.remove("is-touch-line-selection");
    lineDragPointer = null;
    lineScrollPointer = null;
    lineDragLast = 0;
    lineDragTouch = false;
    lineDragGutterX = 0;
    lineDragStartY = 0;
    lineDragClientY = 0;
    lineDragMoved = false;
    lineDragAutoFrame = 0;
    lineScrollLastX = 0;
    lineScrollLastY = 0;
    lineScrollClientY = 0;
    lineDragPrimaryRegion = "";
    lineDragTouchIdentifier = null;
    clearTimeout(codeHoldTimer);
    codeHoldTimer = 0;
    if (commitTouchSelection && selectedLineAnchor) applyStudentLineSelection();
  }

  function beginCodeTouch(event) {
    if (event.pointerType !== "touch") {
      clearStudentLineSelection();
      return;
    }

    const line = Number(event.currentTarget.parentElement?.dataset.line);
    if (!renderedRows[line - 1]) return;

    // 거터가 이미 기준점을 잡았다면 코드 영역의 손가락은 스크롤 전용이다.
    if (lineDragTouch && lineDragPointer !== null && lineDragPrimaryRegion === "gutter") {
      startTouchScroll(event);
      return;
    }

    // 평소 한 손가락 코드 스크롤은 브라우저에 맡긴다. 움직이지 않고 반대 영역에
    // 두 번째 손가락을 댄 경우에만 이 포인터가 줄 선택 기준점으로 승격된다.
    pendingCodeTouch = {
      pointerId: event.pointerId,
      line,
      clientX: event.clientX,
      clientY: event.clientY,
      touchIdentifier: null,
      target: event.currentTarget,
    };
    clearStudentLineSelection();
  }

  function continueCodeTouch(event) {
    if (event.pointerId === lineScrollPointer) {
      continueTouchScroll(event);
      return;
    }
    if (pendingCodeTouch?.pointerId === event.pointerId &&
        (Math.abs(event.clientX - pendingCodeTouch.clientX) >= 8 ||
         Math.abs(event.clientY - pendingCodeTouch.clientY) >= 8)) {
      pendingCodeTouch = null;
      clearTimeout(codeHoldTimer);
      codeHoldTimer = 0;
    }
  }

  function endCodeTouch(event) {
    if (pendingCodeTouch?.pointerId === event.pointerId) {
      pendingCodeTouch = null;
      clearTimeout(codeHoldTimer);
      codeHoldTimer = 0;
    }
    const action = touchPointerEndAction(event.pointerId, lineDragPointer, lineScrollPointer);
    if (action === "release-scroll") {
      stopTouchScroll();
      return;
    }
    if (action === "finish") finishStudentLineDrag();
  }

  function beginTouchLineSelection(pointerId, line, clientY, gutterX, primaryRegion) {
    clearTimeout(followScrollTimer);
    lineDragPointer = pointerId;
    lineDragLast = line;
    lineDragTouch = true;
    lineDragGutterX = gutterX;
    lineDragStartY = clientY;
    lineDragClientY = clientY;
    lineDragPrimaryRegion = primaryRegion;
    selectStudentLines(line, false);
    $("codeScroll").classList.add("is-touch-line-selection");
  }

  function startTouchScroll(event) {
    if (lineScrollPointer !== null) return;
    event.preventDefault();
    lineScrollPointer = event.pointerId;
    lineScrollLastX = event.clientX;
    lineScrollLastY = event.clientY;
    lineScrollClientY = event.clientY;
    event.currentTarget.setPointerCapture?.(event.pointerId);
  }

  function stopTouchScroll() {
    cancelAnimationFrame(lineDragAutoFrame);
    lineDragAutoFrame = 0;
    lineScrollPointer = null;
    lineScrollLastX = 0;
    lineScrollLastY = 0;
    lineScrollClientY = 0;
    lineDragMoved = false;
  }

  function continueTouchScroll(event) {
    event.preventDefault();
    const scroller = $("codeScroll");
    const deltaX = touchDragScrollDelta(lineScrollLastX, event.clientX);
    const deltaY = touchDragScrollDelta(lineScrollLastY, event.clientY);
    lineScrollLastX = event.clientX;
    lineScrollLastY = event.clientY;
    lineScrollClientY = event.clientY;
    if (!deltaX && !deltaY) return;
    lineDragMoved = true;
    scroller.scrollLeft += deltaX;
    scroller.scrollTop += deltaY;
    updateTouchLineSelection();
    scheduleTouchLineAutoScroll();
  }

  function touchDragScrollDelta(previous, current) {
    return previous - current;
  }

  function touchPointerEndAction(pointerId, primaryPointer, scrollPointer) {
    if (pointerId === scrollPointer) return "release-scroll";
    if (pointerId === primaryPointer) return "finish";
    return "ignore";
  }

  function beginCodeHold(event) {
    if (!pendingCodeTouch || lineDragPointer !== null) return;
    const touch = event.changedTouches[0];
    if (!touch) return;
    pendingCodeTouch.touchIdentifier = touch.identifier;
    clearTimeout(codeHoldTimer);
    codeHoldTimer = setTimeout(() => {
      const anchor = pendingCodeTouch;
      if (!anchor || anchor.touchIdentifier !== touch.identifier || lineDragPointer !== null) return;
      pendingCodeTouch = null;
      codeHoldTimer = 0;
      lineDragTouchIdentifier = anchor.touchIdentifier;
      const gutter = anchor.target.parentElement?.firstElementChild;
      const gutterRect = gutter?.getBoundingClientRect();
      if (!gutterRect) return;
      beginTouchLineSelection(anchor.pointerId, anchor.line, anchor.clientY,
        gutterRect.left + gutterRect.width / 2, "code");
    }, 320);
  }

  function continueCodeHold(event) {
    if (lineDragPrimaryRegion !== "code" || lineScrollPointer !== null) return;
    const touch = Array.from(event.touches)
      .find((item) => item.identifier === lineDragTouchIdentifier);
    if (!touch) return;
    event.preventDefault();
    lineDragClientY = touch.clientY;
    if (Math.abs(lineDragClientY - lineDragStartY) >= 6) lineDragMoved = true;
    updateTouchLineSelection();
    scheduleTouchLineAutoScroll();
  }

  function endCodeHold(event) {
    const ended = Array.from(event.changedTouches)
      .some((item) => item.identifier === lineDragTouchIdentifier);
    if (ended && lineDragPrimaryRegion === "code") finishStudentLineDrag();
  }

  function touchEdgeScrollDelta(position, start, end) {
    const zone = Math.min(56, (end - start) / 3);
    if (zone <= 0) return 0;
    if (position < start + zone)
      return -Math.ceil(16 * Math.min(1, (start + zone - position) / zone));
    if (position > end - zone)
      return Math.ceil(16 * Math.min(1, (position - end + zone) / zone));
    return 0;
  }

  function scheduleTouchLineAutoScroll() {
    if (!lineDragTouch || !lineDragMoved || lineDragAutoFrame) return;
    lineDragAutoFrame = requestAnimationFrame(autoScrollTouchLineSelection);
  }

  function autoScrollTouchLineSelection() {
    lineDragAutoFrame = 0;
    if (!lineDragTouch || lineDragPointer === null) return;
    const scroller = $("codeScroll");
    const rect = scroller.getBoundingClientRect();
    const edgeY = lineScrollPointer === null ? lineDragClientY : lineScrollClientY;
    const delta = touchEdgeScrollDelta(edgeY, rect.top, rect.bottom);
    if (!delta) return;
    const before = scroller.scrollTop;
    scroller.scrollTop += delta;
    if (scroller.scrollTop === before) return;
    updateTouchLineSelection();
    lineDragAutoFrame = requestAnimationFrame(autoScrollTouchLineSelection);
  }

  function updateTouchLineSelection() {
    if (!lineDragTouch || lineDragPointer === null) return;
    const rect = $("codeScroll").getBoundingClientRect();
    const y = Math.max(rect.top + 1, Math.min(rect.bottom - 1, lineDragClientY));
    const gutter = document.elementFromPoint(lineDragGutterX, y)?.closest(".ln");
    if (!gutter || !$("codeLines").contains(gutter)) return;
    const line = Number(gutter.parentElement?.dataset.line);
    if (line === lineDragLast || !renderedRows[line - 1]) return;
    lineDragLast = line;
    selectStudentLines(line, true);
  }

  function selectStudentLines(line, extend) {
    if (!renderedRows[line - 1]) return;
    const range = lineSelectionRange(selectedLineAnchor, line, extend);
    selectedLineAnchor = range.anchor;
    selectedLineStart = range.start;
    selectedLineEnd = range.end;
    applyStudentLineSelection();
  }

  function applyStudentLineSelection() {
    for (let line = paintedLineStart; line && line <= paintedLineEnd; line += 1)
      if (line < selectedLineStart || line > selectedLineEnd) setStudentLineSelected(line, false);
    for (let line = selectedLineStart; line <= selectedLineEnd; line += 1)
      if (!paintedLineStart || line < paintedLineStart || line > paintedLineEnd)
        setStudentLineSelected(line, true);
    paintedLineStart = selectedLineStart;
    paintedLineEnd = selectedLineEnd;

    // 터치 드래그 중에는 운영체제의 선택 핸들을 띄우지 않는다. 손을 뗄 때 한 번만 만든다.
    if (lineDragTouch && lineDragPointer !== null) return;
    const first = renderedRows[selectedLineStart - 1]?.node.lastElementChild;
    const last = renderedRows[selectedLineEnd - 1]?.node.lastElementChild;
    if (!first || !last) return;
    const range = document.createRange();
    range.setStart(first, 0);
    range.setEnd(last, last.childNodes.length);
    const selection = getSelection();
    selection?.removeAllRanges();
    selection?.addRange(range);
  }

  function setStudentLineSelected(line, selected) {
    const row = renderedRows[line - 1]?.node;
    if (!row) return;
    row.classList.toggle("is-student-selection", selected);
    row.firstElementChild?.setAttribute("aria-pressed", String(selected));
  }

  function clearStudentLineSelection() {
    if (!selectedLineAnchor) return;
    selectedLineAnchor = 0;
    selectedLineStart = 0;
    selectedLineEnd = 0;
    for (let line = paintedLineStart; line <= paintedLineEnd; line += 1)
      setStudentLineSelected(line, false);
    paintedLineStart = 0;
    paintedLineEnd = 0;
    const selection = getSelection();
    if (selection?.anchorNode && $("codeLines").contains(selection.anchorNode)) selection.removeAllRanges();
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

  // 프로젝트와 파일 노드를 id 기준으로 재사용한다. 폴링할 때마다 다시 만들면
  // 접기 애니메이션과 키보드 포커스가 계속 끊긴다.
  function renderFiles(files, professorActiveId, professorWorkspaceId, professorProjectId) {
    const list = $("fileList");
    const grouped = new Map();
    for (const file of files) {
      const id = file.workspaceId || "workspace";
      if (!grouped.has(id)) grouped.set(id, { id, name: file.workspaceName || t("file.project"), files: [] });
      grouped.get(id).files.push(file);
    }
    const groups = Array.from(grouped.values()).sort((a, b) => a.name.localeCompare(b.name, "ko"));
    list.classList.toggle("has-multiple-workspaces", groups.length > 1);

    const leftover = new Map();
    for (const node of Array.from(list.children)) leftover.set(node.dataset.workspaceId, node);

    groups.forEach((workspace, index) => {
      let group = leftover.get(workspace.id);
      if (group) leftover.delete(workspace.id);
      else group = createWorkspaceGroup(workspace.id);
      updateWorkspaceGroup(group, workspace, professorActiveId,
        professorWorkspaceId, professorProjectId, groups.length > 1);
      if (list.children[index] !== group) list.insertBefore(group, list.children[index] ?? null);
    });

    for (const stale of leftover.values()) stale.remove();
  }

  function createWorkspaceGroup(workspaceId) {
    const group = document.createElement("section");
    group.className = "file-group";
    group.dataset.workspaceId = workspaceId;

    const heading = document.createElement("button");
    heading.type = "button";
    heading.className = "workspace-heading";
    const chevron = document.createElement("span");
    chevron.className = "workspace-chevron";
    chevron.textContent = "›";
    chevron.setAttribute("aria-hidden", "true");
    const name = document.createElement("strong");
    const line = document.createElement("span");
    line.className = "workspace-line";
    line.setAttribute("aria-hidden", "true");
    heading.append(chevron, name, line);

    const body = document.createElement("div");
    body.className = "workspace-body";
    const items = document.createElement("div");
    items.className = "workspace-items";
    body.append(items);
    group.append(heading, body);

    heading.addEventListener("click", () => {
      if (collapsedWorkspaces.has(workspaceId)) collapsedWorkspaces.delete(workspaceId);
      else collapsedWorkspaces.add(workspaceId);
      saveStringSet("classroom-live:collapsed-workspaces", collapsedWorkspaces);
      setWorkspaceCollapsed(group, collapsedWorkspaces.has(workspaceId));
    });
    group.parts = { heading, name, items };
    return group;
  }

  function updateWorkspaceGroup(group, workspace, professorActiveId,
    professorWorkspaceId, professorProjectId, showHeading) {
    const active = workspace.id === professorWorkspaceId;
    group.classList.toggle("is-professor-workspace", active);
    group.classList.toggle("has-heading", showHeading);
    setText(group.parts.name, workspace.name);
    setTitle(group.parts.heading, t(collapsedWorkspaces.has(workspace.id) ? "file.expand" : "file.collapse", { name: workspace.name }));
    setWorkspaceCollapsed(group, showHeading && collapsedWorkspaces.has(workspace.id));
    renderWorkspaceProjects(group.parts.items, workspace, professorActiveId, professorProjectId,
      showHeading ? 1 : 0);
  }

  function setWorkspaceCollapsed(group, collapsed) {
    group.classList.toggle("is-collapsed", collapsed);
    group.parts.heading.setAttribute("aria-expanded", String(!collapsed));
    group.parts.items.setAttribute("aria-hidden", String(collapsed));
    group.parts.items.inert = collapsed;
  }

  function renderWorkspaceProjects(container, workspace, professorActiveId, professorProjectId, baseDepth) {
    const grouped = new Map();
    for (const file of workspace.files) {
      const loose = !file.projectId;
      const id = file.projectId || `${workspace.id}:loose`;
      if (!grouped.has(id)) grouped.set(id, {
        id, name: file.projectName || t("file.misc"), root: file.projectRoot, loose, files: []
      });
      grouped.get(id).files.push(file);
    }
    const projects = Array.from(grouped.values()).sort((a, b) =>
      Number(a.loose) - Number(b.loose) || a.name.localeCompare(b.name, "ko"));
    container.classList.toggle("has-multiple-projects", projects.length > 1);

    const leftover = new Map();
    for (const node of Array.from(container.children)) leftover.set(node.dataset.projectId, node);

    projects.forEach((project, index) => {
      let group = leftover.get(project.id);
      if (group) leftover.delete(project.id);
      else group = createProjectGroup(project.id);
      updateProjectGroup(group, project, professorActiveId,
        professorProjectId, projects.length > 1, baseDepth);
      if (container.children[index] !== group)
        container.insertBefore(group, container.children[index] ?? null);
    });

    for (const stale of leftover.values()) stale.remove();
  }

  function createProjectGroup(projectId) {
    const group = document.createElement("section");
    group.className = "project-group";
    group.dataset.projectId = projectId;

    const heading = document.createElement("button");
    heading.type = "button";
    heading.className = "project-heading";
    const chevron = document.createElement("span");
    chevron.className = "project-chevron";
    chevron.textContent = "›";
    chevron.setAttribute("aria-hidden", "true");
    const name = document.createElement("strong");
    const line = document.createElement("span");
    line.className = "project-line";
    line.setAttribute("aria-hidden", "true");
    heading.append(chevron, name, line);

    const body = document.createElement("div");
    body.className = "project-body";
    const items = document.createElement("div");
    items.className = "project-items";
    body.append(items);
    group.append(heading, body);

    heading.addEventListener("click", () => {
      if (collapsedProjects.has(projectId)) collapsedProjects.delete(projectId);
      else collapsedProjects.add(projectId);
      saveStringSet("classroom-live:collapsed-projects", collapsedProjects);
      setProjectCollapsed(group, collapsedProjects.has(projectId));
    });
    group.parts = { heading, name, items };
    return group;
  }

  function updateProjectGroup(group, project, professorActiveId, professorProjectId, showHeading, baseDepth) {
    group.classList.toggle("has-heading", showHeading);
    group.classList.toggle("is-professor-project", project.id === professorProjectId);
    group.parts.heading.style.setProperty("--tree-depth", baseDepth);
    setText(group.parts.name, project.name);
    setTitle(group.parts.heading, t(collapsedProjects.has(project.id) ? "file.expand" : "file.collapse", { name: project.name }));
    setProjectCollapsed(group, showHeading && collapsedProjects.has(project.id));
    renderProjectFiles(group.parts.items, project, professorActiveId,
      baseDepth + (showHeading ? 1 : 0));
  }

  function setProjectCollapsed(group, collapsed) {
    group.classList.toggle("is-collapsed", collapsed);
    group.parts.heading.setAttribute("aria-expanded", String(!collapsed));
    group.parts.items.setAttribute("aria-hidden", String(collapsed));
    group.parts.items.inert = collapsed;
  }

  function renderProjectFiles(container, project, professorActiveId, baseDepth) {
    const rows = fileTreeRows(project);
    const leftover = new Map();
    for (const node of Array.from(container.children)) leftover.set(node.dataset.nodeKey, node);

    rows.forEach((row, index) => {
      const nodeKey = row.type === "file" ? `file:${row.file.id}` : `folder:${row.key}`;
      let item = leftover.get(nodeKey);
      if (item) leftover.delete(nodeKey);
      else item = row.type === "file" ? createFileItem(row.file) : createFolderItem(row.key);
      item.dataset.nodeKey = nodeKey;
      if (row.type === "file") updateFileItem(item, row.file, professorActiveId, row.depth + baseDepth);
      else updateFolderItem(item, { ...row, depth: row.depth + baseDepth });
      if (container.children[index] !== item) container.insertBefore(item, container.children[index] ?? null);
    });

    for (const stale of leftover.values()) stale.remove();
  }

  function fileTreeRows(project) {
    const root = { folders: new Map(), files: [] };
    // Visual Studio와 Windows는 파일 경로의 대소문자를 구분하지 않는다. 편집기가
    // 같은 폴더를 Src/src처럼 다른 표기로 보내도 트리에 중복 노드가 생기지 않게 한다.
    const pathKey = (value) => String(value).toLocaleLowerCase("en-US");
    for (const file of project.files) {
      const parts = String(file.path || file.name).replaceAll("\\", "/").split("/").filter(Boolean);
      parts.pop();
      const hasRoot = typeof project.root === "string";
      const rootParts = project.root === "." ? [] :
        String(project.root || "").replaceAll("\\", "/").split("/").filter(Boolean);
      const matchesRoot = rootParts.length > 0 && rootParts.every((part, index) =>
        pathKey(parts[index]) === pathKey(part));
      if (!project.loose && matchesRoot) parts.splice(0, rootParts.length);
      else if (!project.loose && !hasRoot) {
        // 1.3.4 이전 호스트 데이터에는 실제 프로젝트 루트가 없으므로 이름으로 추정한다.
        const projectRoot = parts.findIndex((part) => pathKey(part) === pathKey(project.name));
        if (projectRoot >= 0) parts.splice(0, projectRoot + 1);
      }
      let node = root;
      parts.forEach((name, index) => {
        const normalizedName = pathKey(name);
        if (!node.folders.has(normalizedName)) node.folders.set(normalizedName, {
          name,
          path: parts.slice(0, index + 1).join("/"),
          keyPath: parts.slice(0, index + 1).map(pathKey).join("/"),
          folders: new Map(), files: []
        });
        node = node.folders.get(normalizedName);
      });
      node.files.push(file);
    }

    const rows = [];
    const sortedFiles = (files) => files.slice().sort((a, b) => a.name.localeCompare(b.name, "ko"));
    const sortedFolders = (folders) => Array.from(folders.values())
      .sort((a, b) => a.name.localeCompare(b.name, "ko"));
    const appendFolder = (folder, depth) => {
      // 공유 파일 없이 다음 폴더 하나로만 이어지는 중간 단계는 목록에서 생략한다.
      // 생략한 폴더는 들여쓰기에도 반영하지 않아 좁은 목록 폭을 아낀다.
      const meaningful = folder.files.length > 0 || folder.folders.size > 1;
      if (!meaningful) {
        for (const child of sortedFolders(folder.folders)) appendFolder(child, depth);
        return;
      }
      const key = `${project.id}:${folder.keyPath}`;
      rows.push({ type: "folder", key, name: folder.name, path: folder.path, depth });
      if (collapsedFolders.has(key)) return;
      for (const file of sortedFiles(folder.files)) rows.push({ type: "file", file, depth: depth + 1 });
      for (const child of sortedFolders(folder.folders)) appendFolder(child, depth + 1);
    };

    for (const file of sortedFiles(root.files)) rows.push({ type: "file", file, depth: 0 });
    for (const folder of sortedFolders(root.folders)) appendFolder(folder, 0);
    return rows;
  }

  function createFolderItem(folderKey) {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "folder-item";
    item.dataset.folderKey = folderKey;
    const chevron = document.createElement("span");
    chevron.className = "folder-chevron";
    chevron.textContent = "›";
    chevron.setAttribute("aria-hidden", "true");
    const name = document.createElement("strong");
    item.append(chevron, name);
    item.addEventListener("click", () => {
      const key = item.dataset.folderKey;
      if (collapsedFolders.has(key)) collapsedFolders.delete(key);
      else collapsedFolders.add(key);
      saveStringSet("classroom-live:collapsed-folders", collapsedFolders);
      if (latestClassroom) renderFiles(latestClassroom.files,
        latestClassroom.professorActiveId, latestClassroom.professorWorkspaceId,
        latestClassroom.professorProjectId);
    });
    item.parts = { chevron, name };
    return item;
  }

  function updateFolderItem(item, row) {
    const collapsed = collapsedFolders.has(row.key);
    item.dataset.folderKey = row.key;
    item.style.setProperty("--tree-depth", row.depth);
    item.classList.toggle("is-collapsed", collapsed);
    item.setAttribute("aria-expanded", String(!collapsed));
    setText(item.parts.name, row.name);
    setTitle(item, `${row.path} · ${t(collapsed ? "file.expand" : "file.collapse", { name: row.name })}`);
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
      closeFiles();
      // 직접 골랐다는 건 안내를 이해했다는 뜻이다.
      dismissNote();
      if (latestClassroom) render(latestClassroom, latestHostState);
      if (!shuttingDown) void refresh();
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
    badge.textContent = t("professor.label");
    badge.hidden = true;
    open.append(icon, copy, badge);
    item.append(open);

    let remove = null;
    let hide = null;
    if (isHost) {
      hide = document.createElement("button");
      hide.type = "button";
      hide.className = "hide-file";
      hide.setAttribute("aria-pressed", "false");
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

  function updateFileItem(item, file, professorActiveId, depth = 0) {
    const { open, icon, name, updated, badge, remove, hide } = item.parts;
    const isSelected = file.id === selectedId;

    const className = `file-item${isSelected ? " is-selected" : ""}${file.hidden ? " is-hidden-file" : ""}${file.pending ? " is-pending-file" : ""}${file.missing ? " is-missing-file" : ""}`;
    if (item.className !== className) item.className = className;
    item.style.setProperty("--tree-depth", depth);
    if (isSelected) open.setAttribute("aria-current", "page");
    else open.removeAttribute("aria-current");

    const pendingText = file.missing ? t("file.missing") : t("file.openInVs");
    setTitle(open, file.pending ? `${file.path} · ${pendingText}` : `${file.path} · ${file.language}`);
    open.disabled = Boolean(file.pending);
    setText(icon, shortLanguage(file.language));
    setText(name, file.name);
    setText(updated, file.pending ? pendingText : relativeTime(file.updatedAt));
    badge.hidden = file.id !== professorActiveId;
    if (remove) remove.setAttribute("aria-label", t(latestClassroom?.everStarted ? "file.remove" : "file.removePrepared", { name: file.name }));
    if (hide) {
      // 아이콘은 상태를, aria-pressed는 눌린 상태(=숨김)를 나타낸다.
      const isHidden = Boolean(file.hidden);
      hide.dataset.hidden = isHidden ? "1" : "0";
      hide.setAttribute("aria-pressed", String(isHidden));
      hide.setAttribute("aria-label", t("file.hide", { name: file.name }));
      setTitle(hide, t(isHidden ? "file.hidden.title" : "file.visible.title"));
    }
  }

  function renderHost(payload) {
    setText($("pinValue"), payload.pin);
    // 한 번도 시작한 적 없으면 "시작", 돌다가 멈췄으면 "재개"로 구분한다.
    const broadcastButton = $("toggleBroadcast");
    setText(broadcastButton, payload.broadcasting
      ? t("action.pause")
      : payload.everStarted ? t("action.resume") : t("action.start"));
    // 재개만 노란색으로 구분하고 시작·일시정지는 기본 초록색을 쓴다.
    broadcastButton.classList.toggle("is-resume", !payload.broadcasting && payload.everStarted);
    const hostStatus = translateServerText(payload.visualStudioStatus,
      payload.visualStudioStatusArgument ? { name: payload.visualStudioStatusArgument } : {});
    setText($("hostStatus"), hostStatus);
    setTitle($("hostStatus"), hostStatus);
    if (payload.restoreAvailable) showRestore(Number(payload.restoreFileCount) || 0);

    // 현재 Visual Studio 파일을 공유 목록의 +로 추가한다. 제거는 각 파일의 ×가 담당한다.
    const share = $("toggleShare");
    const label = $("addFileLabel");
    const current = payload.currentFileName;
    share.hidden = !current;
    label.hidden = !current;
    if (!current) return;

    const shared = Boolean(payload.currentFileShared);
    const shareable = Boolean(payload.currentFileShareable);
    const displayPath = payload.currentFileDisplayPath || current;
    const canAdd = shareable && !shared;
    setText(label, current);
    setTitle(label, displayPath);
    setText(share, shared ? "✓" : "+");
    const reason = translateServerText(payload.currentFileBlockReason) || t("file.notShareable");
    const action = shared ? t("file.alreadyAdded", { name: current })
      : shareable ? t("file.add", { name: current }) : reason;
    share.setAttribute("aria-label", action);
    setTitle(share, action);
    share.classList.toggle("is-active", shared);
    share.classList.toggle("is-blocked", !shared && !shareable);
    share.dataset.shared = shared ? "1" : "0";
    share.dataset.shareable = shareable ? "1" : "0";
    share.dataset.reason = shareable ? "" : reason;
    share.disabled = !canAdd;
  }

  function setConnection(text, kind) {
    const element = $("connection");
    const className = `connection is-${kind}`;
    if (element.className !== className) element.className = className;
    $("statusLive").className = `status-live is-${kind}`;
    $("professorFile").parentElement.className = `is-${kind}`;
    setText(element.querySelector("b"), text);
  }

  function setSessionState(state) {
    const view = SESSION_STATES[state];
    currentSessionState = state;
    setText($("className"), t(`state.${view.key}.name`));
    setConnection(t(`state.${view.key}.connection`), view.kind);
    setText($("syncStatus"), t(`state.${view.key}.sync`));
    $("viewerCount").parentElement.hidden = !view.viewers;
  }

  function loadStringSet(key) {
    try {
      const values = JSON.parse(localStorage.getItem(key) || "[]");
      return new Set(Array.isArray(values) ? values : []);
    } catch {
      return new Set();
    }
  }

  function saveStringSet(key, values) {
    localStorage.setItem(key, JSON.stringify(Array.from(values)));
  }

  function shortLanguage(language) {
    return ({ "C#": "C#", "C++": "C++", JavaScript: "JS", TypeScript: "TS", Python: "PY" })[language] || language.slice(0, 3).toUpperCase();
  }

  function relativeTime(value) {
    const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
    if (seconds < 10) return t("time.now");
    if (seconds < 60) return plural("time.seconds", seconds);
    if (seconds < 3600) return plural("time.minutes", Math.floor(seconds / 60));
    return plural("time.hours", Math.floor(seconds / 3600));
  }

  function translateServerText(value, values = {}) {
    if (!value) return "";
    if (catalog[value]) return t(value, values);
    const exact = {
      "연결 대기": "host.status.waiting",
      "Visual Studio 연결 대기": "host.status.waiting",
      "공유할 수 없는 파일": "file.notShareable",
      "공유할 수 없는 파일입니다": "file.notShareable",
    };
    return exact[value] ? t(exact[value]) : value;
  }

  void initializeLocalization().then(refresh);
  setInterval(refresh, 750);
})();
