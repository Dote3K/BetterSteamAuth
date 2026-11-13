CREATE TABLE vault_meta (
  key   TEXT PRIMARY KEY,
  value BLOB NOT NULL
);

CREATE TABLE proxies (
  id             INTEGER PRIMARY KEY,
  name           TEXT,
  scheme         TEXT NOT NULL CHECK (scheme IN ('http','https','socks5')),
  host           TEXT NOT NULL,
  port           INTEGER NOT NULL,
  username       TEXT,
  password_enc   BLOB,
  password_nonce BLOB,
  status         TEXT NOT NULL DEFAULT 'unknown',
  last_check_at  INTEGER,
  latency_ms     INTEGER,
  last_error     TEXT,
  UNIQUE (scheme, host, port, username)
);

CREATE TABLE groups (
  id   INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE
);

CREATE TABLE accounts (
  steam_id     INTEGER PRIMARY KEY,
  account_name TEXT,
  filename     TEXT NOT NULL UNIQUE,
  proxy_id     INTEGER REFERENCES proxies(id) ON DELETE SET NULL,
  group_id     INTEGER REFERENCES groups(id)  ON DELETE SET NULL,
  note         TEXT,
  sort_order   INTEGER NOT NULL DEFAULT 0,
  enabled      INTEGER NOT NULL DEFAULT 1,
  created_at   INTEGER NOT NULL,
  updated_at   INTEGER NOT NULL
);
CREATE INDEX ix_accounts_proxy ON accounts(proxy_id);
CREATE INDEX ix_accounts_group ON accounts(group_id);

CREATE TABLE device_profiles (
  steam_id        INTEGER PRIMARY KEY REFERENCES accounts(steam_id) ON DELETE CASCADE,
  model           TEXT NOT NULL,
  friendly_name   TEXT NOT NULL,
  android_version TEXT NOT NULL,
  build_id        TEXT NOT NULL,
  machine_id      BLOB NOT NULL,
  created_at      INTEGER NOT NULL
);

CREATE TABLE auto_confirm_rules (
  steam_id  INTEGER PRIMARY KEY REFERENCES accounts(steam_id) ON DELETE CASCADE,
  trades    INTEGER NOT NULL DEFAULT 0,
  market    INTEGER NOT NULL DEFAULT 0,
  whitelist TEXT
);

CREATE TABLE confirmation_log (
  id        INTEGER PRIMARY KEY,
  steam_id  INTEGER NOT NULL REFERENCES accounts(steam_id) ON DELETE CASCADE,
  conf_id   TEXT NOT NULL,
  conf_type INTEGER NOT NULL,
  summary   TEXT,
  action    TEXT NOT NULL,
  source    TEXT NOT NULL,
  ok        INTEGER NOT NULL,
  at        INTEGER NOT NULL
);
CREATE INDEX ix_conflog_account_time ON confirmation_log(steam_id, at);

CREATE TABLE settings (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
