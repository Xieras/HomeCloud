(() => {
  const $ = (s, r = document) => r.querySelector(s);
  const body = document.body;
  const CSRF = body.dataset.csrf, PATH = body.dataset.path || "", CAN_WRITE = body.dataset.write === "1";

  function toast(msg, kind) {
    const t = $("#toast");
    t.textContent = msg; t.className = "toast " + (kind || ""); t.hidden = false;
    clearTimeout(toast.t); toast.t = setTimeout(() => (t.hidden = true), 4500);
  }

  // Диалог: с полем ввода (value != null) или простое подтверждение
  function ask({ title, text = "", value = null, ok = "OK", danger = false }) {
    return new Promise(res => {
      const d = $("#dlg"), i = $("#dlg-input"), b = $("#dlg-ok");
      $("#dlg-title").textContent = title; $("#dlg-text").textContent = text;
      i.hidden = value === null; i.value = value ?? "";
      b.textContent = ok; b.className = "btn " + (danger ? "danger" : "dark");
      d.onclose = () => res(d.returnValue === "ok" ? (value === null ? true : i.value.trim()) : null);
      d.returnValue = ""; d.showModal();
      if (value !== null) { i.focus(); i.select(); }
    });
  }

  async function call(url, data) {
    const fd = new FormData();
    for (const k in data) fd.append(k, data[k]);
    try {
      const r = await fetch(url, { method: "POST", body: fd, headers: { "X-CSRF-Token": CSRF } });
      const j = await r.json().catch(() => ({}));
      if (!r.ok || j.error) { toast(j.error || "Ошибка " + r.status, "err"); return; }
      location.reload();
    } catch { toast("Нет связи с сервером", "err"); }
  }

  const fmt = n => { const u = ["Б", "КБ", "МБ", "ГБ", "ТБ"]; let i = 0; while (n >= 1024 && i < 4) { n /= 1024; i++; } return n.toFixed(i ? 1 : 0) + " " + u[i]; };

  function upload(files) {
    if (!CAN_WRITE || !files.length) return;
    const fd = new FormData();
    for (const f of files) fd.append("files", f);
    const box = $("#up"), bar = $("#up-bar"), txt = $("#up-text");
    const x = new XMLHttpRequest();
    x.open("POST", "/upload?p=" + encodeURIComponent(PATH));
    x.setRequestHeader("X-CSRF-Token", CSRF);
    box.hidden = false; bar.value = 0;
    x.upload.onprogress = e => {
      if (!e.lengthComputable) return;
      bar.value = Math.round(e.loaded / e.total * 100);
      txt.textContent = `Загружено ${bar.value}% — ${fmt(e.loaded)} из ${fmt(e.total)}`;
    };
    x.onload = () => {
      if (x.status === 200) return location.reload();
      box.hidden = true;
      let m = "Ошибка загрузки"; try { m = JSON.parse(x.responseText).error || m; } catch {}
      toast(m, "err");
    };
    x.onerror = () => { box.hidden = true; toast("Загрузка прервана", "err"); };
    x.send(fd);
  }

  document.addEventListener("click", async e => {
    const b = e.target.closest("[data-act]");
    if (b) {
      const { act, path = "", name = "" } = b.dataset;
      if (act === "pick") $("#pick").click();
      if (act === "mkdir") { const n = await ask({ title: "Новая папка", value: "", ok: "Создать" }); if (n) call("/mkdir", { p: PATH, name: n }); }
      if (act === "rename") { const n = await ask({ title: "Переименовать", value: name, ok: "Сохранить" }); if (n && n !== name) call("/rename", { p: path, name: n }); }
      if (act === "delete") {
        const dir = b.dataset.dir === "1";
        const ok = await ask({ title: "Удалить «" + name + "»?", text: dir ? "Папка будет удалена вместе со всем содержимым." : "Файл будет удалён без возможности восстановления.", ok: "Удалить", danger: true });
        if (ok) call("/delete", { p: path });
      }
    }
    const g = e.target.closest("[data-gen]");
    if (g) {
      const abc = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
      const r = crypto.getRandomValues(new Uint32Array(14));
      g.closest(".pw").querySelector("input").value = Array.from(r, v => abc[v % abc.length]).join("");
    }
  });

  document.addEventListener("submit", async e => {
    const t = e.target.dataset && e.target.dataset.confirm;
    if (t && !e.target.dataset.ok) {
      e.preventDefault();
      if (await ask({ title: "Подтвердите действие", text: t, ok: "Удалить", danger: true })) { e.target.dataset.ok = "1"; e.target.submit(); }
    }
  });

  const pick = $("#pick");
  if (pick) pick.addEventListener("change", () => upload(pick.files));

  const drop = $("#drop");
  if (drop && CAN_WRITE) {
    let depth = 0;
    const has = e => e.dataTransfer && Array.from(e.dataTransfer.types).includes("Files");
    addEventListener("dragenter", e => { if (has(e)) { e.preventDefault(); depth++; drop.classList.add("on"); } });
    addEventListener("dragover", e => { if (has(e)) e.preventDefault(); });
    addEventListener("dragleave", () => { if (--depth <= 0) { depth = 0; drop.classList.remove("on"); } });
    addEventListener("drop", e => { if (!has(e)) return; e.preventDefault(); depth = 0; drop.classList.remove("on"); upload(e.dataTransfer.files); });
  }

  const filter = $("#filter");
  if (filter) filter.addEventListener("input", () => {
    const q = filter.value.trim().toLowerCase();
    document.querySelectorAll("tr[data-n]").forEach(r => (r.hidden = q && !r.dataset.n.includes(q)));
  });
})();
