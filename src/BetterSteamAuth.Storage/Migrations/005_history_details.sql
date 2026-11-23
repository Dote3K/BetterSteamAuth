-- Steam's summary lines with each history entry (give/receive counts, price, payout), sealed like the headline.
ALTER TABLE confirmation_log ADD COLUMN details TEXT;
ALTER TABLE confirmation_log ADD COLUMN details_enc BLOB;
ALTER TABLE confirmation_log ADD COLUMN details_nonce BLOB;
