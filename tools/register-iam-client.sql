-- Register the WhatsApp Bridge as a public OAuth2/OIDC client in the IAM System (OpenIddict).
-- Run on the IAM Postgres instance: psql -h 127.0.0.1 -U iam_user -d iam_db
-- Public client + PKCE (S256) — no client secret exists or is needed; nothing here is a
-- credential that belongs in vault. Mirrors the same pattern used by coach-app, jengo-web,
-- portofgiethoorn, etc. (see yinyogasound-coach/tools/register-iam-client.sql).

DO $$
DECLARE
    app_id TEXT := gen_random_uuid()::TEXT;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM "OpenIddictApplications" WHERE "ClientId" = 'whatsapp-bridge') THEN
        INSERT INTO "OpenIddictApplications" (
            "Id",
            "ClientId",
            "ClientType",
            "ConsentType",
            "DisplayName",
            "Permissions",
            "RedirectUris",
            "PostLogoutRedirectUris",
            "Requirements",
            "ConcurrencyToken"
        ) VALUES (
            app_id,
            'whatsapp-bridge',
            'public',
            'implicit',
            'WhatsApp Bridge',
            '["ept:authorization","ept:token","gt:authorization_code","gt:refresh_token","rst:code","scp:openid","scp:profile","scp:email","scp:roles"]',
            '["https://whatsapp.wreckingball.ai/api/auth/iam/callback","http://localhost:5149/api/auth/iam/callback"]',
            '["https://whatsapp.wreckingball.ai/login","http://localhost:5173/login"]',
            '["ft:pkce"]',
            gen_random_uuid()::TEXT
        );
        RAISE NOTICE 'whatsapp-bridge client created with id: %', app_id;
    ELSE
        RAISE NOTICE 'whatsapp-bridge already exists, nothing to do.';
    END IF;
END $$;
