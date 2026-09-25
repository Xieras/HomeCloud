"""HomeCloud — личное файловое хранилище (Flask + SQLite)."""
import hashlib, logging, mimetypes, os, re, secrets, shutil, sqlite3
from datetime import datetime, timedelta
from functools import wraps
from pathlib import Path

import click
from dotenv import load_dotenv
from flask import (Flask, abort, flash, g, jsonify, redirect, render_template,
                   request, send_file, session, url_for)
from markupsafe import Markup
from werkzeug.exceptions import HTTPException
from werkzeug.middleware.proxy_fix import ProxyFix
from werkzeug.security import check_password_hash, generate_password_hash

load_dotenv(Path(__file__).with_name(".env"))

STORAGE = Path(os.environ.get("STORAGE_ROOT", "/srv/storage")).resolve()
DB_PATH = os.environ.get("DB_PATH", "/var/lib/homecloud/app.db")
SITE = os.environ.get("SITE_NAME", "HomeCloud")
FMT = "%Y-%m-%d %H:%M:%S"
EMAIL_RE = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")
DUMMY_HASH = generate_password_hash("dummy-password")
MAX_FAILS, LOCK_MIN = 5, 15

app = Flask(__name__)
app.secret_key = os.environ["SECRET_KEY"]
app.config.update(
    SESSION_COOKIE_NAME="hc_session", SESSION_COOKIE_HTTPONLY=True,
    SESSION_COOKIE_SAMESITE="Lax",
    SESSION_COOKIE_SECURE=os.environ.get("COOKIE_SECURE", "1") == "1",
    PERMANENT_SESSION_LIFETIME=timedelta(hours=12),
)
app.wsgi_app = ProxyFix(app.wsgi_app, x_for=1, x_proto=1, x_host=1)

# --- лог для fail2ban -------------------------------------------------------
auth_log = logging.getLogger("hc.auth")
auth_log.setLevel(logging.INFO)
try:
    _h = logging.FileHandler(os.environ.get("AUTH_LOG", "/var/log/homecloud/auth.log"))
except OSError:
    _h = logging.StreamHandler()
_h.setFormatter(logging.Formatter("%(asctime)s %(message)s", FMT))
auth_log.addHandler(_h)

# --- БД ---------------------------------------------------------------------
SCHEMA = """
CREATE TABLE IF NOT EXISTS users(
  id INTEGER PRIMARY KEY, email TEXT UNIQUE NOT NULL, pw_hash TEXT NOT NULL,
  perm TEXT NOT NULL DEFAULT 'read', root TEXT NOT NULL DEFAULT '',
  is_admin INTEGER NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL, last_login TEXT);
CREATE TABLE IF NOT EXISTS logs(
  id INTEGER PRIMARY KEY, ts TEXT NOT NULL, email TEXT, action TEXT NOT NULL,
  detail TEXT, ip TEXT);
CREATE INDEX IF NOT EXISTS logs_ts ON logs(ts);
"""


def now():
    return datetime.now().strftime(FMT)


def db():
    if "db" not in g:
        g.db = sqlite3.connect(DB_PATH, timeout=10)
        g.db.row_factory = sqlite3.Row
    return g.db


@app.teardown_appcontext
def close_db(_):
    d = g.pop("db", None)
    if d:
        d.close()


def init_db():
    STORAGE.mkdir(parents=True, exist_ok=True)
    Path(DB_PATH).parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(DB_PATH)
    con.executescript(SCHEMA)
    email = os.environ.get("ADMIN_EMAIL", "").strip().lower()
    pw = os.environ.get("ADMIN_PASSWORD")
    if email and pw and not con.execute("SELECT 1 FROM users WHERE is_admin=1").fetchone():
        con.execute("INSERT INTO users(email,pw_hash,perm,is_admin,created_at) VALUES(?,?,?,?,?)",
                    (email, generate_password_hash(pw), "write", 1, now()))
    con.commit()
    con.close()


init_db()


def log(action, detail="", email=None):
    u = g.get("user")
    db().execute("INSERT INTO logs(ts,email,action,detail,ip) VALUES(?,?,?,?,?)",
                 (now(), email or (u["email"] if u else "-"), action, detail, request.remote_addr))
    db().commit()


# --- шаблонные помощники ----------------------------------------------------
ICONS = {
    "cloud": '<path d="M7 18a4 4 0 0 1-.6-7.96A6 6 0 0 1 18 9.5 4.25 4.25 0 0 1 17.5 18z"/>',
    "folder": '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    "download": '<path d="M12 3v12m0 0l-4-4m4 4l4-4M4 19h16"/>',
    "upload": '<path d="M12 15V3m0 0L8 7m4-4l4 4M4 19h16"/>',
    "edit": '<path d="M4 20h4L19 9l-4-4L4 16z"/>',
    "trash": '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>',
    "plus": '<path d="M12 5v14M5 12h14"/>',
    "logout": '<path d="M9 4H5v16h4M16 8l4 4-4 4M20 12H9"/>',
    "users": '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0M16 4.5a3.5 3.5 0 0 1 0 7M18 14a6.5 6.5 0 0 1 3.5 6"/>',
    "list": '<path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01"/>',
    "key": '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9m-3 3l3 3"/>',
    "up": '<path d="M9 14l-4-4 4-4M5 10h9a5 5 0 0 1 5 5v3"/>',
}


@app.template_global()
def ic(name):
    return Markup('<svg class="ic" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" '
                  'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">%s</svg>' % ICONS[name])


@app.template_global()
def csrf_token():
    if "csrf" not in session:
        session["csrf"] = secrets.token_urlsafe(32)
    return session["csrf"]


LABELS = {
    "login_ok": ("Вход", "ok"), "login_fail": ("Неудачный вход", "bad"), "logout": ("Выход", ""),
    "upload": ("Загрузка", "info"), "download": ("Скачивание", "info"), "mkdir": ("Новая папка", "info"),
    "rename": ("Переименование", "info"), "delete": ("Удаление", "bad"),
    "user_create": ("Выдан доступ", "warn"), "user_update": ("Права изменены", "warn"),
    "user_delete": ("Доступ удалён", "bad"), "pw_change": ("Смена пароля", "warn"),
    "pw_reset": ("Сброс пароля", "warn"),
}


@app.template_global()
def label(action):
    return LABELS.get(action, (action, ""))


@app.template_filter("size")
def human(n):
    n = float(n or 0)
    for u in ("Б", "КБ", "МБ", "ГБ", "ТБ"):
        if n < 1024 or u == "ТБ":
            return f"{n:.0f} {u}" if u == "Б" else f"{n:.1f} {u}"
        n /= 1024


@app.context_processor
def inject():
    disk = None
    if g.get("user") and g.user["is_admin"]:
        du = shutil.disk_usage(STORAGE)
        disk = {"used": du.used, "total": du.total, "free": du.free, "pct": round(du.used / du.total * 100)}
    return {"site": SITE, "can_write": can_write(), "disk": disk}


# --- аутентификация ---------------------------------------------------------
def fp(u):
    return hashlib.sha256(u["pw_hash"].encode()).hexdigest()[:16]


def can_write(u=None):
    u = u or g.get("user")
    return bool(u and (u["is_admin"] or u["perm"] == "write"))


@app.before_request
def load_user():
    g.user = None
    uid = session.get("uid")
    if uid:
        u = db().execute("SELECT * FROM users WHERE id=? AND active=1", (uid,)).fetchone()
        if u and session.get("fp") == fp(u):
            g.user = u
        else:
            session.clear()
    if request.method == "POST":
        tok = request.headers.get("X-CSRF-Token") or request.form.get("csrf_token") or ""
        if not secrets.compare_digest(tok, session.get("csrf", "")):
            abort(400, "Сессия устарела — обновите страницу и повторите.")


@app.after_request
def secure_headers(r):
    r.headers["X-Content-Type-Options"] = "nosniff"
    r.headers["X-Frame-Options"] = "DENY"
    r.headers["Referrer-Policy"] = "same-origin"
    r.headers["Content-Security-Policy"] = ("default-src 'self'; img-src 'self' data:; media-src 'self'; "
                                            "object-src 'self'; frame-ancestors 'none'")
    if r.mimetype == "text/html":
        r.headers["Cache-Control"] = "no-store"
    return r


def login_required(f):
    @wraps(f)
    def w(*a, **k):
        if not g.user:
            if request.method == "GET":
                return redirect(url_for("login"))
            return jsonify(error="Требуется вход"), 401
        return f(*a, **k)
    return w


def write_required(f):
    @wraps(f)
    def w(*a, **k):
        if not can_write():
            return jsonify(error="Недостаточно прав: у вас доступ только для чтения"), 403
        return f(*a, **k)
    return w


def admin_required(f):
    @wraps(f)
    def w(*a, **k):
        if not g.user:
            return redirect(url_for("login"))
        if not g.user["is_admin"]:
            abort(404)
        return f(*a, **k)
    return w


@app.errorhandler(HTTPException)
def http_error(e):
    if request.method == "POST":
        return jsonify(error=e.description), e.code
    return render_template("error.html", e=e), e.code


@app.route("/login", methods=["GET", "POST"])
def login():
    if g.user:
        return redirect(url_for("files"))
    if request.method == "POST":
        email = request.form.get("email", "").strip().lower()
        pw = request.form.get("password", "")
        since = (datetime.now() - timedelta(minutes=LOCK_MIN)).strftime(FMT)
        fails = db().execute("SELECT COUNT(*) FROM logs WHERE action='login_fail' AND ip=? AND ts>?",
                             (request.remote_addr, since)).fetchone()[0]
        if fails >= MAX_FAILS:
            flash(f"Слишком много попыток. Подождите {LOCK_MIN} минут.", "err")
            return render_template("login.html"), 429
        u = db().execute("SELECT * FROM users WHERE email=?", (email,)).fetchone()
        ok = check_password_hash(u["pw_hash"] if u else DUMMY_HASH, pw) and u and u["active"]
        if not ok:
            log("login_fail", "", email=email[:120])
            auth_log.info("LOGIN_FAIL ip=%s email=%s", request.remote_addr, email[:120])
            flash("Неверная почта или пароль.", "err")
            return render_template("login.html"), 401
        session.clear()
        session.permanent = True
        session.update(uid=u["id"], fp=fp(u), csrf=secrets.token_urlsafe(32))
        g.user = u
        db().execute("UPDATE users SET last_login=? WHERE id=?", (now(), u["id"]))
        log("login_ok")
        auth_log.info("LOGIN_OK ip=%s email=%s", request.remote_addr, email)
        return redirect(url_for("files"))
    return render_template("login.html")


@app.post("/logout")
@login_required
def logout():
    log("logout")
    session.clear()
    return redirect(url_for("login"))


@app.route("/account", methods=["GET", "POST"])
@login_required
def account():
    if request.method == "POST":
        cur, new, rep = (request.form.get(k, "") for k in ("current", "new", "repeat"))
        if not check_password_hash(g.user["pw_hash"], cur):
            flash("Текущий пароль указан неверно.", "err")
        elif len(new) < 8:
            flash("Новый пароль — минимум 8 символов.", "err")
        elif new != rep:
            flash("Пароли не совпадают.", "err")
        else:
            h = generate_password_hash(new)
            db().execute("UPDATE users SET pw_hash=? WHERE id=?", (h, g.user["id"]))
            db().commit()
            g.user = db().execute("SELECT * FROM users WHERE id=?", (g.user["id"],)).fetchone()
            session["fp"] = fp(g.user)
            log("pw_change")
            flash("Пароль изменён.", "ok")
        return redirect(url_for("account"))
    return render_template("account.html")


# --- файлы ------------------------------------------------------------------
def clean_name(n):
    return re.sub(r'[\x00-\x1f/\\:*?"<>|]', "_", (n or "").strip()).strip(". ")[:200]


def clean_root(r):
    return "/".join(c for c in (clean_name(x) for x in (r or "").split("/")) if c)


def unique(p):
    if not p.exists():
        return p
    i = 1
    while (q := p.with_name(f"{p.stem} ({i}){p.suffix}")).exists():
        i += 1
    return q


def resolve(rel):
    """Возвращает (корень пользователя, абсолютный путь) и не даёт выйти за корень."""
    root = g.user["root"] or ""
    base = (STORAGE / root).resolve() if root else STORAGE
    if base != STORAGE and STORAGE not in base.parents:
        abort(403)
    base.mkdir(parents=True, exist_ok=True)
    p = (base / (rel or "").strip("/")).resolve()
    if p != base and base not in p.parents:
        abort(403, "Доступ запрещён")
    return base, p


def rel_of(base, p):
    r = p.relative_to(base).as_posix()
    return "" if r == "." else r


@app.get("/")
def index():
    return redirect(url_for("files"))


@app.get("/files")
@login_required
def files():
    base, p = resolve(request.args.get("p", ""))
    if not p.is_dir():
        abort(404, "Папка не найдена")
    rel = rel_of(base, p)
    items = []
    with os.scandir(p) as it:
        for e in it:
            if e.name.startswith("."):
                continue
            try:
                st, d = e.stat(), e.is_dir()
            except OSError:
                continue
            items.append({"name": e.name, "dir": d, "size": st.st_size,
                          "mtime": datetime.fromtimestamp(st.st_mtime).strftime("%d.%m.%Y %H:%M"),
                          "rel": f"{rel}/{e.name}" if rel else e.name,
                          "ext": Path(e.name).suffix[1:5] or "file"})
    items.sort(key=lambda i: (not i["dir"], i["name"].lower()))
    crumbs, acc = [], ""
    for part in [x for x in rel.split("/") if x]:
        acc = f"{acc}/{part}" if acc else part
        crumbs.append((part, acc))
    parent = rel.rsplit("/", 1)[0] if "/" in rel else ""
    return render_template("files.html", items=items, rel=rel, crumbs=crumbs, parent=parent)


@app.get("/download")
@login_required
def download():
    base, p = resolve(request.args.get("p", ""))
    if not p.is_file():
        abort(404, "Файл не найден")
    mt = mimetypes.guess_type(p.name)[0] or ""
    previewable = (mt.startswith(("image/", "video/", "audio/")) and mt != "image/svg+xml") or mt == "application/pdf"
    rng = request.headers.get("Range", "")
    if not rng or rng.startswith("bytes=0-"):
        log("download", rel_of(base, p))
    return send_file(p, as_attachment=not (previewable and request.args.get("inline") == "1"),
                     download_name=p.name, conditional=True)


@app.post("/upload")
@login_required
@write_required
def upload():
    base, d = resolve(request.args.get("p", ""))
    if not d.is_dir():
        abort(404)
    saved = []
    for f in request.files.getlist("files"):
        name = clean_name(f.filename)
        if name:
            t = unique(d / name)
            f.save(t)
            saved.append(t.name)
    log("upload", f"/{rel_of(base, d)}: {', '.join(saved)}")
    return jsonify(ok=True, saved=saved)


@app.post("/mkdir")
@login_required
@write_required
def mkdir():
    base, d = resolve(request.form.get("p", ""))
    name = clean_name(request.form.get("name"))
    if not name or not d.is_dir():
        return jsonify(error="Некорректное имя папки"), 400
    if (d / name).exists():
        return jsonify(error="Такое имя уже занято"), 409
    (d / name).mkdir()
    log("mkdir", "/" + rel_of(base, d / name))
    return jsonify(ok=True)


@app.post("/rename")
@login_required
@write_required
def rename():
    base, p = resolve(request.form.get("p", ""))
    name = clean_name(request.form.get("name"))
    if p == base or not p.exists() or not name:
        return jsonify(error="Некорректное имя"), 400
    t = p.with_name(name)
    if t.exists():
        return jsonify(error="Такое имя уже занято"), 409
    old = rel_of(base, p)
    p.rename(t)
    log("rename", f"/{old} → /{rel_of(base, t)}")
    return jsonify(ok=True)


@app.post("/delete")
@login_required
@write_required
def delete():
    base, p = resolve(request.form.get("p", ""))
    if p == base or not p.exists():
        return jsonify(error="Нечего удалять"), 400
    rel = rel_of(base, p)
    shutil.rmtree(p) if p.is_dir() and not p.is_symlink() else p.unlink()
    log("delete", "/" + rel)
    return jsonify(ok=True)


# --- админ-панель -----------------------------------------------------------
@app.get("/admin")
@admin_required
def admin():
    users = db().execute("SELECT * FROM users ORDER BY is_admin DESC, email").fetchall()
    today = datetime.now().strftime("%Y-%m-%d")
    q = lambda sql, *a: db().execute(sql, a).fetchone()[0]
    stats = {
        "users": len(users),
        "logins": q("SELECT COUNT(*) FROM logs WHERE action='login_ok' AND ts>=?", today),
        "fails": q("SELECT COUNT(*) FROM logs WHERE action='login_fail' AND ts>=?", today),
        "actions": q("SELECT COUNT(*) FROM logs WHERE ts>=? AND action IN "
                     "('upload','download','delete','rename','mkdir')", today),
    }
    rows = db().execute("SELECT * FROM logs ORDER BY id DESC LIMIT 8").fetchall()
    return render_template("admin.html", users=users, stats=stats, rows=rows)


@app.post("/admin/users")
@admin_required
def admin_create():
    email = request.form.get("email", "").strip().lower()
    pw, perm = request.form.get("password", ""), request.form.get("perm")
    root = clean_root(request.form.get("root"))
    if not EMAIL_RE.match(email) or perm not in ("read", "write") or len(pw) < 8:
        flash("Проверьте данные: корректная почта, пароль от 8 символов, тип доступа.", "err")
    elif db().execute("SELECT 1 FROM users WHERE email=?", (email,)).fetchone():
        flash("Пользователь с такой почтой уже есть.", "err")
    else:
        db().execute("INSERT INTO users(email,pw_hash,perm,root,created_at) VALUES(?,?,?,?,?)",
                     (email, generate_password_hash(pw), perm, root, now()))
        db().commit()
        log("user_create", f"{email}: {perm}, папка /{root}")
        flash(f"Доступ выдан: {email}", "ok")
    return redirect(url_for("admin"))


def target(uid):
    u = db().execute("SELECT * FROM users WHERE id=?", (uid,)).fetchone()
    if not u:
        abort(404)
    if u["is_admin"]:
        abort(403, "Администратора нельзя менять из панели")
    return u


@app.post("/admin/users/<int:uid>/update")
@admin_required
def admin_update(uid):
    u = target(uid)
    perm = request.form.get("perm")
    if perm not in ("read", "write"):
        abort(400)
    root, active = clean_root(request.form.get("root")), 1 if request.form.get("active") else 0
    db().execute("UPDATE users SET perm=?, root=?, active=? WHERE id=?", (perm, root, active, uid))
    db().commit()
    log("user_update", f"{u['email']}: {perm}, папка /{root}, {'включён' if active else 'отключён'}")
    flash("Права сохранены.", "ok")
    return redirect(url_for("admin"))


@app.post("/admin/users/<int:uid>/password")
@admin_required
def admin_password(uid):
    u = target(uid)
    pw = request.form.get("password", "")
    if len(pw) < 8:
        flash("Пароль — минимум 8 символов.", "err")
    else:
        db().execute("UPDATE users SET pw_hash=? WHERE id=?", (generate_password_hash(pw), uid))
        db().commit()
        log("pw_reset", u["email"])
        flash(f"Новый пароль для {u['email']} установлен.", "ok")
    return redirect(url_for("admin"))


@app.post("/admin/users/<int:uid>/delete")
@admin_required
def admin_delete(uid):
    u = target(uid)
    db().execute("DELETE FROM users WHERE id=?", (uid,))
    db().commit()
    log("user_delete", u["email"])
    flash(f"Доступ удалён: {u['email']}", "ok")
    return redirect(url_for("admin"))


@app.get("/admin/logs")
@admin_required
def admin_logs():
    q, action = request.args.get("q", "").strip(), request.args.get("action", "")
    page, per = max(1, request.args.get("page", 1, type=int)), 50
    where, args = [], []
    if q:
        where.append("(email LIKE ? OR detail LIKE ? OR ip LIKE ?)")
        args += [f"%{q}%"] * 3
    if action:
        where.append("action=?")
        args.append(action)
    w = "WHERE " + " AND ".join(where) if where else ""
    total = db().execute(f"SELECT COUNT(*) FROM logs {w}", args).fetchone()[0]
    rows = db().execute(f"SELECT * FROM logs {w} ORDER BY id DESC LIMIT ? OFFSET ?",
                        args + [per, (page - 1) * per]).fetchall()
    actions = [r[0] for r in db().execute("SELECT DISTINCT action FROM logs ORDER BY 1")]
    return render_template("logs.html", rows=rows, q=q, action=action, actions=actions,
                           page=page, pages=max(1, -(-total // per)), total=total)


# --- CLI: восстановление доступа с сервера ----------------------------------
@app.cli.command("set-password")
@click.argument("email")
def cli_set_password(email):
    """Задать новый пароль (в т.ч. администратору, если забыл)."""
    pw = click.prompt("Новый пароль", hide_input=True, confirmation_prompt=True)
    with sqlite3.connect(DB_PATH) as con:
        n = con.execute("UPDATE users SET pw_hash=?, active=1 WHERE email=?",
                        (generate_password_hash(pw), email.lower())).rowcount
    click.echo("Готово" if n else "Пользователь не найден")


@app.cli.command("create-admin")
@click.argument("email")
def cli_create_admin(email):
    """Создать администратора или назначить им существующего пользователя."""
    email = email.lower()
    with sqlite3.connect(DB_PATH) as con:
        if con.execute("SELECT 1 FROM users WHERE email=?", (email,)).fetchone():
            con.execute("UPDATE users SET is_admin=1, active=1 WHERE email=?", (email,))
        else:
            pw = click.prompt("Пароль", hide_input=True, confirmation_prompt=True)
            con.execute("INSERT INTO users(email,pw_hash,perm,is_admin,created_at) VALUES(?,?,?,?,?)",
                        (email, generate_password_hash(pw), "write", 1, now()))
    click.echo("Готово")
