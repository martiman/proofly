-- Canonical records are immutable once ingested (corrections arrive as a new delivery that supersedes).
-- jobs table is reserved for #17 (queue); do not use the name for anything else.
CREATE TABLE transfer_instructions (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  source_row_id text NOT NULL,
  instruction_id text NOT NULL,
  client_account_id text NOT NULL,
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  transaction_type text NOT NULL,
  amount numeric(38,6) NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  instruction_timestamp timestamptz NOT NULL,
  requested_value_date date,
  source_status text NOT NULL,
  external_reference text,
  destination_reference text,
  UNIQUE (delivery_id, source_row_id)
);
CREATE INDEX ix_transfer_instr_instruction ON transfer_instructions (instruction_id);
CREATE INDEX ix_transfer_instr_account ON transfer_instructions (client_account_id, requested_value_date);
CREATE INDEX ix_transfer_instr_status ON transfer_instructions (source_status);

CREATE TABLE direct_debit_events (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  source_row_id text NOT NULL,
  instruction_id text NOT NULL,
  mandate_reference text,
  client_account_id text NOT NULL,
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  amount numeric(38,6) NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  collection_date date,
  event_type text NOT NULL,
  event_timestamp timestamptz NOT NULL,
  source_status text NOT NULL,
  return_reason text,
  original_instruction_reference text,
  UNIQUE (delivery_id, source_row_id)
);
CREATE INDEX ix_dd_events_instruction ON direct_debit_events (instruction_id);
CREATE INDEX ix_dd_events_account ON direct_debit_events (client_account_id, collection_date);
CREATE INDEX ix_dd_events_status ON direct_debit_events (source_status);

CREATE TABLE card_events (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  source_row_id text NOT NULL,
  card_event_id text NOT NULL,
  card_transaction_id text NOT NULL,
  client_account_id text NOT NULL,
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  tokenized_card_reference text,
  event_type text NOT NULL,
  event_timestamp timestamptz NOT NULL,
  amount numeric(38,6) NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  debit_credit_indicator text NOT NULL CHECK (debit_credit_indicator IN ('DEBIT','CREDIT')),
  merchant_reference text,
  settlement_id text,
  original_transaction_id text,
  source_status text NOT NULL,
  UNIQUE (delivery_id, source_row_id)
);
CREATE INDEX ix_card_events_tx ON card_events (card_transaction_id);
CREATE INDEX ix_card_events_settlement ON card_events (settlement_id);
CREATE INDEX ix_card_events_account ON card_events (client_account_id, event_timestamp);

CREATE TABLE card_settlements (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  source_row_id text NOT NULL,
  settlement_id text NOT NULL,
  processor text NOT NULL,
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  settlement_date date NOT NULL,
  gross_amount numeric(38,6) NOT NULL,
  fees numeric(38,6) NOT NULL,
  adjustments numeric(38,6) NOT NULL,
  net_amount numeric(38,6) NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  destination_account text,
  source_status text NOT NULL,
  UNIQUE (delivery_id, source_row_id)
);
CREATE INDEX ix_card_settlements_settlement ON card_settlements (settlement_id);

CREATE TABLE ledger_entries (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  ledger_entry_id text NOT NULL,
  account_id text NOT NULL,
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  booking_timestamp timestamptz NOT NULL,
  value_date date NOT NULL,
  debit_credit_indicator text NOT NULL CHECK (debit_credit_indicator IN ('DEBIT','CREDIT')),
  amount numeric(38,6) NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  transaction_reference text,
  entry_type text NOT NULL,
  source_status text NOT NULL,
  UNIQUE (delivery_id, ledger_entry_id)
);
CREATE INDEX ix_ledger_account ON ledger_entries (account_id, value_date);
CREATE INDEX ix_ledger_reference ON ledger_entries (transaction_reference);

CREATE TABLE account_balances (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  account_id text NOT NULL,
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  balance_date date NOT NULL,
  balance_type text NOT NULL,
  opening_balance_amount numeric(38,6),
  balance_amount numeric(38,6) NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  source_timestamp timestamptz,
  UNIQUE (delivery_id, account_id, balance_type, balance_date)
);
CREATE INDEX ix_account_balances_account ON account_balances (account_id, balance_date);

CREATE TABLE bank_transactions (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  external_transaction_id text NOT NULL,
  external_account_id text NOT NULL,
  booking_timestamp timestamptz NOT NULL,
  value_date date NOT NULL,
  amount numeric(38,6) NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  debit_credit_indicator text NOT NULL CHECK (debit_credit_indicator IN ('DEBIT','CREDIT')),
  bank_reference text,
  transaction_status text NOT NULL,
  source_timestamp timestamptz,
  UNIQUE (delivery_id, external_transaction_id)
);
CREATE INDEX ix_bank_tx_account ON bank_transactions (external_account_id, value_date);
CREATE INDEX ix_bank_tx_reference ON bank_transactions (bank_reference);

CREATE TABLE safeguarding_balances (
  id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  delivery_id text NOT NULL REFERENCES source_deliveries(id),
  source_row_id text NOT NULL,
  external_account_id text NOT NULL,
  legal_entity_id text NOT NULL REFERENCES legal_entities(id),
  balance_timestamp timestamptz NOT NULL,
  balance_type text NOT NULL,
  balance_amount numeric(38,6) NOT NULL,
  currency char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  evidence_reference text,
  source_timestamp timestamptz,
  UNIQUE (delivery_id, source_row_id)
);
CREATE INDEX ix_sg_balances_account ON safeguarding_balances (external_account_id, balance_timestamp);
