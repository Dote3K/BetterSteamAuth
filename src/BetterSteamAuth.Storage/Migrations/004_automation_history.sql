-- Per-account automation (replaces the never-used auto_confirm_rules).
DROP TABLE IF EXISTS auto_confirm_rules;
CREATE TABLE account_automation (
  steam_id  INTEGER PRIMARY KEY REFERENCES accounts(steam_id) ON DELETE CASCADE,
  checks    INTEGER NOT NULL DEFAULT 0,  -- 0 like settings, 1 always, 2 never
  notify    INTEGER NOT NULL DEFAULT 1,
  trades    INTEGER NOT NULL DEFAULT 0,
  market    INTEGER NOT NULL DEFAULT 0,
  purchases INTEGER NOT NULL DEFAULT 0
);

-- History survives removing an account (Restore brings it back) and gets an encrypted summary for vaults with a
-- password. Plain summaries already logged in such vaults are sealed at the next unlock.
CREATE TABLE confirmation_log_v2 (
  id            INTEGER PRIMARY KEY,
  steam_id      INTEGER NOT NULL,
  conf_id       TEXT NOT NULL,
  conf_type     INTEGER NOT NULL,
  summary       TEXT,
  summary_enc   BLOB,
  summary_nonce BLOB,
  action        TEXT NOT NULL,  -- accept | deny
  source        TEXT NOT NULL,  -- manual | auto
  ok            INTEGER NOT NULL,
  at            INTEGER NOT NULL
);
INSERT INTO confirmation_log_v2(id, steam_id, conf_id, conf_type, summary, action, source, ok, at)
  SELECT id, steam_id, conf_id, conf_type, summary, action, source, ok, at FROM confirmation_log;
DROP TABLE confirmation_log;
ALTER TABLE confirmation_log_v2 RENAME TO confirmation_log;
CREATE INDEX ix_conflog_account_time ON confirmation_log(steam_id, at);
