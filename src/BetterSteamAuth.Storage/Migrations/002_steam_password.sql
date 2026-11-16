-- Optional saved Steam password for automatic sign-in, AES-GCM with the vault DEK.
ALTER TABLE accounts ADD COLUMN password_enc BLOB;
ALTER TABLE accounts ADD COLUMN password_nonce BLOB;
