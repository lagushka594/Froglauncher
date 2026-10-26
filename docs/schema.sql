CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;

CREATE TABLE entitlements_cache (
    user_id uuid NOT NULL,
    json jsonb NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_entitlements_cache PRIMARY KEY (user_id)
);

CREATE TABLE refresh_tokens (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    token_hash character varying(64) NOT NULL,
    family_id uuid NOT NULL,
    audience character varying(16) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    replaced_by_id uuid,
    CONSTRAINT pk_refresh_tokens PRIMARY KEY (id)
);

CREATE TABLE staff_accounts (
    id uuid NOT NULL,
    login character varying(64) NOT NULL,
    password_hash text NOT NULL,
    role character varying(32) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_staff_accounts PRIMARY KEY (id)
);

CREATE TABLE users (
    id uuid NOT NULL,
    launcher_nick character varying(32) NOT NULL,
    email character varying(254) NOT NULL,
    password_hash text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    token_version integer NOT NULL,
    CONSTRAINT pk_users PRIMARY KEY (id)
);

CREATE TABLE webhook_events (
    id uuid NOT NULL,
    provider character varying(16) NOT NULL,
    event_id character varying(128) NOT NULL,
    event_type character varying(64) NOT NULL,
    received_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_webhook_events PRIMARY KEY (id)
);

CREATE TABLE admin_grants (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    granted_by character varying(64) NOT NULL,
    granted_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    revoked_by character varying(64),
    reason character varying(500) NOT NULL,
    revoke_reason character varying(500),
    CONSTRAINT pk_admin_grants PRIMARY KEY (id),
    CONSTRAINT fk_admin_grants_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
);

CREATE TABLE subscriptions (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    tier_slug character varying(32) NOT NULL,
    period character varying(8) NOT NULL,
    status character varying(16) NOT NULL,
    provider character varying(16) NOT NULL,
    provider_sub_id character varying(128),
    provider_payment_method_id character varying(128),
    current_period_end timestamp with time zone,
    grace_until timestamp with time zone,
    cancel_at_period_end boolean NOT NULL,
    replaces_subscription_id uuid,
    failed_renewal_attempts integer NOT NULL,
    created_at timestamp with time zone NOT NULL,
    canceled_at timestamp with time zone,
    CONSTRAINT pk_subscriptions PRIMARY KEY (id),
    CONSTRAINT fk_subscriptions_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
);

CREATE TABLE payments (
    id uuid NOT NULL,
    subscription_id uuid NOT NULL,
    user_id uuid NOT NULL,
    provider character varying(16) NOT NULL,
    provider_payment_id character varying(128),
    tier_slug text NOT NULL,
    period character varying(8) NOT NULL,
    amount numeric(12,2) NOT NULL,
    currency character varying(3) NOT NULL,
    status character varying(16) NOT NULL,
    kind character varying(16) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_payments PRIMARY KEY (id),
    CONSTRAINT fk_payments_subscriptions_subscription_id FOREIGN KEY (subscription_id) REFERENCES subscriptions (id) ON DELETE CASCADE
);

CREATE INDEX ix_admin_grants_user_id_revoked_at ON admin_grants (user_id, revoked_at);

CREATE UNIQUE INDEX ix_payments_provider_provider_payment_id ON payments (provider, provider_payment_id) WHERE provider_payment_id IS NOT NULL;

CREATE INDEX ix_payments_subscription_id ON payments (subscription_id);

CREATE INDEX ix_payments_user_id ON payments (user_id);

CREATE UNIQUE INDEX ix_refresh_tokens_token_hash ON refresh_tokens (token_hash);

CREATE INDEX ix_refresh_tokens_user_id_revoked_at ON refresh_tokens (user_id, revoked_at);

CREATE UNIQUE INDEX ix_staff_accounts_login ON staff_accounts (login);

CREATE UNIQUE INDEX ix_subscriptions_provider_provider_sub_id ON subscriptions (provider, provider_sub_id) WHERE provider_sub_id IS NOT NULL;

CREATE INDEX ix_subscriptions_status_current_period_end ON subscriptions (status, current_period_end);

CREATE INDEX ix_subscriptions_user_id_status ON subscriptions (user_id, status);

CREATE UNIQUE INDEX ix_users_email ON users (email);

CREATE UNIQUE INDEX ix_users_launcher_nick ON users (launcher_nick);

CREATE UNIQUE INDEX ix_webhook_events_provider_event_id ON webhook_events (provider, event_id);

INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
VALUES ('20261026122510_InitialSubscriptions', '8.0.10');

COMMIT;

START TRANSACTION;

ALTER TABLE payments ADD granted_by character varying(64);

ALTER TABLE payments ADD note character varying(500);

INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
VALUES ('20261026133927_ManualGrants', '8.0.10');

COMMIT;

