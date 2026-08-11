-- ==========================================================================
-- Schema + extensions (citext, btree_gist, pgcrypto)
-- Generated from `pg_dump --schema-only --no-owner` of a database built by
-- applying all 134 pre-baseline migrations in order. Do not hand-edit:
-- regenerate with MigrationsBaseline/regenerate-baseline-sql.py, then re-run
-- the schema-parity diff.
--
-- Every statement is IDEMPOTENT. Production is still at the pre-squash head with
-- the whole schema already materialised, and Program.cs applies migrations
-- uncaught at boot, so a bare CREATE here is a failed deploy. Objects with no
-- IF NOT EXISTS form are wrapped in a DO block that checks pg_catalog for that
-- exact object - never a broader condition that could skip a policy or a
-- constraint the database is genuinely missing.
-- ==========================================================================

--
-- PostgreSQL database dump
--


-- Dumped from database version 16.14
-- Dumped by pg_dump version 16.14

SET LOCAL statement_timeout = 0;
SET LOCAL lock_timeout = 0;
SET LOCAL idle_in_transaction_session_timeout = 0;
SET LOCAL client_encoding = 'UTF8';
SET LOCAL standard_conforming_strings = on;
SELECT pg_catalog.set_config('nexora.squashed_baseline_saved_search_path', current_setting('search_path'), true);
SELECT pg_catalog.set_config('search_path', '', true);
SET LOCAL check_function_bodies = false;
SET LOCAL xmloption = content;
SET LOCAL client_min_messages = warning;
SET LOCAL row_security = off;

--
-- Name: platform; Type: SCHEMA; Schema: -; Owner: -
--

CREATE SCHEMA IF NOT EXISTS platform;


--
-- Name: btree_gist; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS btree_gist WITH SCHEMA public;


--
-- Name: EXTENSION btree_gist; Type: COMMENT; Schema: -; Owner: -
--



--
-- Name: citext; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS citext WITH SCHEMA public;


--
-- Name: EXTENSION citext; Type: COMMENT; Schema: -; Owner: -
--



--
-- Name: pgcrypto; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public;


--
-- Name: EXTENSION pgcrypto; Type: COMMENT; Schema: -; Owner: -
--
