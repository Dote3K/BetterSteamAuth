-- What Restore needs to bring a removed account back with its metadata; the maFile itself is in .trash/.
CREATE TABLE removed_accounts (
  id             INTEGER PRIMARY KEY,
  steam_id       INTEGER NOT NULL,
  account_name   TEXT,                -- NULL in encrypted vaults (read from the file when unlocked)
  trash_file     TEXT NOT NULL UNIQUE,
  reason         TEXT NOT NULL,       -- removed | link_cancelled | authenticator_removed
  removed_at     INTEGER NOT NULL,
  proxy_id       INTEGER,             -- no FK: the proxy may be gone by the time of restore
  group_name     TEXT,
  note           TEXT,
  password_enc   BLOB,
  password_nonce BLOB,
  device_profile TEXT                 -- JSON of the device_profiles row, or NULL
);
