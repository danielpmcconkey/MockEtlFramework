# Analysis Progress

Written ONLY by reviewers. Updated after each BRD review.

| Job Name | Analyst | Review Status | Review Cycles | Notes |
|----------|---------|---------------|---------------|-------|
| communication_channel_map | analyst-5 | PASS | 1 | Approved -- thorough analysis with well-verified evidence |
| customer_contact_info | analyst-5 | PASS | 1 | Approved -- clean SQL-only job analysis, good catch on unused segments table |
| compliance_event_summary | analyst-3 | PASS | 1 | Approved -- thorough analysis of Sunday skip, dead-end accounts, trailer config, dictionary non-determinism |
| covered_transactions | analyst-7 | PASS | 1 | Approved -- thorough analysis of External module with 13 business rules, all evidence verified |
| bond_maturity_schedule | analyst-2 | PASS | 1 | Approved -- strong evidence chains, good catch on misleading job name and cross-date aggregation |
| daily_transaction_summary | analyst-7 | PASS | 2 | Approved -- revision corrects header-in-Append behavior, clean SQL-only analysis |
| customer_contactability | analyst-5 | PASS | 1 | Approved -- weekend fallback logic, conditional date filtering, dead columns/tables well-documented |
| account_balance_snapshot | analyst-10 | PASS | 1 | Approved -- clean passthrough job, all evidence verified, good open questions on unused sources |
| customer_investment_summary | analyst-2 | PASS | 1 | Approved -- Banker's rounding, dead sources, cross-date inflation well-documented; minor line ref error noted |
| daily_transaction_volume | analyst-7 | PASS | 2 | Approved -- revision corrects header-in-Append, CRLF, non-deterministic timestamp trailer |
| do_not_contact_list | analyst-5 | PASS | 1 | Approved -- all-opted-out logic, Sunday skip, multi-date accumulation well-documented |
| debit_credit_ratio | analyst-7 | PASS | 1 | Approved -- excellent analysis of W4 integer division and W6 double-precision quirks, all 11 rules verified |
| compliance_open_items | analyst-3 | PASS | 1 | Approved -- dual-filter logic (date + status), weekend fallback, dead columns well-documented |
| monthly_transaction_trend | analyst-7 | PASS | 2 | Approved -- revision corrects header-in-Append, hardcoded date filter, no trailer |
| compliance_resolution_time | analyst-3 | PASS | 1 | Approved -- outstanding cross-join inflation analysis with mathematical proof, unused ROW_NUMBER, integer truncation |
| compliance_transaction_ratio | analyst-3 | PASS | 1 | Approved -- direct file I/O pattern, inflated trailer, integer division, empty sharedState output all well-documented |
| customer_compliance_risk | analyst-3 | PASS | 1 | Approved -- outstanding account_id/customer_id mismatch bug discovery, risk formula, threshold analysis |
| payment_channel_mix | analyst-7 | PASS | 1 | Approved -- clean UNION ALL aggregation, non-deterministic row ordering correctly flagged |
| branch_card_activity | analyst-8 | PASS | 1 | Approved -- modulo branch assignment, unused sources well-documented |
| branch_directory | analyst-8 | PASS | 1 | Approved -- non-deterministic ROW_NUMBER ordering correctly identified |
| branch_transaction_volume | analyst-8 | PASS | 1 | Approved -- naming mismatch (account-level not branch), unused sources flagged |
| branch_visit_log | analyst-8 | PASS | 1 | Approved -- External module enrichment, last-write-wins, unused addresses |
| branch_visit_purpose_breakdown | analyst-8 | PASS | 1 | Approved -- computed-but-unused window function, unused segments |
| branch_visit_summary | analyst-8 | PASS | 1 | Approved -- clean SQL aggregation with trailer |
| customer_branch_activity | analyst-8 | PASS | 1 | Approved -- cross-date aggregation, single as_of, unused branches |
| customer_transaction_activity | analyst-8 | PASS | 1 | Approved -- decimal arithmetic (no rounding), customer_id=0 skip, cross-date agg |
| dormant_account_detection | analyst-8 | PASS | 1 | Approved -- weekend fallback, multi-date duplication, as_of string formatting |
| email_opt_in_rate | analyst-5 | PASS | 1 | Approved -- integer division bug in rate calculation, dead phone_numbers, clean SQL analysis |
| top_branches | analyst-8 | PASS | 1 | Approved -- non-date-aligned join duplication, RANK(), hardcoded date filter |
| large_transaction_log | analyst-3 | PASS | 1 | Approved -- two-step lookup, dead addresses, unused account columns, amount>500 boundary, Append mode |
| fund_allocation_breakdown | analyst-2 | PASS | 1 | Approved -- direct file I/O, stale trailer date bug (W8), correct output row count, dead investments |
| marketing_eligible_customers | analyst-5 | PASS | 1 | Approved -- all-3-channels requirement, weekend fallback, conditional date filter, dead columns |
| credit_score_average | analyst-6 | PASS | 1 | Approved -- three-bureau averaging, case-insensitive switch, unused segments, DBNull defaults |
| account_customer_join | analyst-10 | PASS | 1 | Approved -- clean denormalization join, addresses sourced but unused, last-write-wins on multi-date |
| account_status_summary | analyst-10 | PASS | 1 | Approved -- group by (type,status), segments sourced but unused, Dictionary iteration order noted |
| account_type_distribution | analyst-10 | PASS | 1 | Approved -- double-precision percentage, branches sourced but unused, END trailer prefix |
| account_velocity_tracking | analyst-10 | PASS | 1 | Approved -- direct file I/O (W12), repeated headers on append, no framework writer, excellent documentation |
| customer_full_profile | analyst-10 | PASS | 1 | Approved -- 5 source tables, age calculation, first-encountered phone/email, comma-separated segments |
| daily_balance_movement | analyst-10 | PASS | 1 | Approved -- W6 double arithmetic bug, W9 Overwrite mode bug, no rounding applied |
| executive_dashboard | analyst-10 | PASS | 1 | Approved -- 9 metrics, 7 sources, guard clause asymmetry, non-deterministic timestamp trailer |
| high_balance_accounts | analyst-10 | PASS | 1 | Approved -- strict > 10000 threshold, decimal comparison, account_status sourced but excluded |
| holdings_by_sector | analyst-2 | PASS | 1 | Approved -- direct file I/O, inflated trailer count bug (W7), sector lookup with Unknown default, all 10 rules verified |
| preference_by_segment | analyst-5 | PASS | 1 | Approved -- Banker's rounding, inflated trailer (W7), three-table join with Unknown defaults, last-write-wins non-determinism |
| investment_account_overview | analyst-2 | PASS | 1 | Approved -- Sunday skip, customer name lookup, row-level as_of, unused prefix/suffix/advisor_id, CsvFileWriter with trailer |
| credit_score_snapshot | analyst-6 | PASS | 1 | Approved -- clean pass-through, branches sourced but unused, CRLF line endings |
| customer_demographics | analyst-6 | PASS | 1 | Approved -- age calculation, first phone/email, segments unused, 11 business rules verified |
| customer_segment_map | analyst-6 | PASS | 1 | Approved -- SQL-only INNER JOIN on segment_id+as_of, branches unused, Append mode |
| customer_address_history | analyst-6 | PASS | 1 | Approved -- SQL-only, NULL customer_id filter, address_id excluded, Append Parquet |
| customer_address_deltas | analyst-6 | PASS | 1 | Approved -- outstanding self-sourcing module, 16 business rules, NEW/UPDATED detection, no DELETE |
| customer_credit_summary | analyst-6 | PASS | 1 | Approved -- compound 4-source guard, DBNull avg_credit_score, segments unused |
| customer_value_score | analyst-6 | PASS | 1 | Approved -- 3-component weighted scoring, capped at 1000, orphan txn skip, 12 rules verified |
| customer_360_snapshot | analyst-6 | PASS | 1 | Approved -- weekend fallback, in-code date filtering, 4 source aggregations |
| customer_attrition_signals | analyst-6 | PASS | 1 | Approved -- weighted binary attrition score, W6 double arithmetic, risk thresholds, 10 rules |
| cross_sell_candidates | analyst-6 | PASS | 1 | Approved -- asymmetric product representations, missing_products excludes investment, 10 rules |
| account_overdraft_history | analyst-4 | PASS | 1 | Approved -- SQL INNER JOIN on account_id+as_of, Parquet 50 parts, unused sourced columns |
| fee_revenue_daily | analyst-4 | PASS | 1 | Approved -- hardcoded date range, double precision (W6), monthly total sums full range not just month |
| fee_waiver_analysis | analyst-4 | PASS | 1 | Approved -- dead-end LEFT JOIN to accounts, NULL coalescing, row duplication risk |
| overdraft_amount_distribution | analyst-4 | PASS | 1 | Approved -- direct file I/O, bucket boundaries, inflated trailer (W7), Environment.NewLine |
| overdraft_by_account_type | analyst-4 | PASS | 1 | Approved -- integer division (W4) always-zero rate, account count inflation, Unknown overdrafts silently lost |
| overdraft_customer_profile | analyst-4 | PASS | 1 | Approved -- weekend fallback (W2), dead-end accounts (AP1), unused customer columns (AP4), decimal precision |
| overdraft_daily_summary | analyst-4 | PASS | 1 | Approved -- weekly total scope bug (W3a), dead-end transactions (AP1), all fees included |
| overdraft_fee_summary | analyst-4 | PASS | 1 | Approved -- unused ROW_NUMBER CTE, no NULL coalescing, clean SQL analysis |
| overdraft_recovery_rate | analyst-4 | PASS | 1 | Approved -- integer division (W4) + banker's rounding (W5) dual-bug, single-row output |
| repeat_overdraft_customers | analyst-4 | PASS | 1 | Approved -- magic threshold 2+ (AP7), cross-date counting, decimal arithmetic, unordered output |
| daily_wire_volume | analyst-9 | PASS | 1 | Approved -- SQL-only, hard-coded dates, redundant WHERE, duplicate as_of column, Append mode |
| large_wire_report | analyst-9 | PASS | 1 | Approved -- amount > 10000 strict, banker's rounding (W5), customer name lookup, no status/direction filter |
| suspicious_wire_flags | analyst-9 | PASS | 1 | Approved -- OFFSHORE case-sensitive, HIGH_AMOUNT >50000, mutually exclusive flags, empty output with current data |
| wire_direction_summary | analyst-9 | PASS | 1 | Approved -- direct file I/O, inflated trailer (W7), append:false Overwrite, as_of from first row |
| wire_transfer_daily | analyst-9 | PASS | 1 | Approved -- MONTHLY_TOTAL on month-end, mixed-type wire_date column, accounts unused, null as_of skip |
| loan_portfolio_snapshot | analyst-9 | PASS | 1 | Approved -- simple pass-through excluding origination_date/maturity_date, branches unused |
| loan_risk_assessment | analyst-9 | PASS | 1 | Approved -- risk tiers, DBNull+Unknown for missing scores, compound guard, decimal Average() |
| inter_account_transfers | analyst-9 | PASS | 1 | Approved -- O(n^2) matching, HashSet+break for single-match, accounts unused, non-deterministic pairings |
| card_authorization_summary | analyst-1 | PASS | 1 | Approved -- integer division approval_rate (W4), dead ROW_NUMBER + unused_summary CTEs, INNER JOIN |
| card_customer_spending | analyst-1 | PASS | 1 | Approved -- weekend fallback (W2), date filtering, dead accounts sourcing, 12 rules verified |
| card_expiration_watch | analyst-1 | PASS | 1 | Approved -- 0-90 day window, weekend fallback, DateTime handling, duplicate across snapshots |
| card_fraud_flags | analyst-1 | PASS | 1 | Approved -- dual filter (High + >$500), Banker's rounding at boundary, magic threshold (AP7) |
| card_spending_by_merchant | analyst-1 | PASS | 1 | Approved -- MCC aggregation, as_of from first row, declined txns included, unused columns |
| card_status_snapshot | analyst-1 | PASS | 1 | Approved -- clean SQL GROUP BY, 50 numParts for ~3 rows, weekday-only cards data |
| card_transaction_daily | analyst-1 | PASS | 1 | Approved -- MONTHLY_TOTAL (W3b), dead accounts/customers (AP1), 13 rules verified |
| card_type_distribution | analyst-1 | PASS | 1 | Approved -- double epsilon (W6) pct_of_total, dead card_transactions, fraction not percentage |
| high_risk_merchant_activity | analyst-1 | PASS | 1 | Approved -- likely empty output (high-risk MCCs not in transaction data), no amount threshold |
| merchant_category_directory | analyst-1 | PASS | 1 | Approved -- only Append mode job, header suppression verified against CsvFileWriter source, dead cards |
| peak_transaction_times | analyst-7 | PASS | 1 | Approved -- direct file I/O (W7), hourly bucketing, UTF-8 BOM difference, timestamp parsing fallback |
| transaction_anomaly_flags | analyst-7 | PASS | 1 | Approved -- 3-sigma anomaly detection, mixed decimal/double precision, banker's rounding, dead customers |
| transaction_category_summary | analyst-7 | PASS | 1 | Approved -- vestigial CTE window functions, Append with correct header suppression, unused segments |
| transaction_size_buckets | analyst-7 | PASS | 1 | Approved -- 5 amount buckets, string sort on bucket names, unused ROW_NUMBER, no trailer |
| product_penetration | analyst-10 | PASS | 1 | Approved -- integer division bug (W4), cross-join for as_of, LIMIT 3, unused customer names |
| quarterly_executive_kpis | analyst-10 | PASS | 1 | Approved -- weekend fallback (dead code), guard on customers only, 8 KPIs, overlap with ExecutiveDashboard |
| wealth_tier_analysis | analyst-10 | PASS | 1 | Approved -- 4 wealth tiers, banker's rounding, totalCustomers from wealth dict not customers table, 12 rules |
| monthly_revenue_breakdown | analyst-10 | PASS | 1 | Approved -- quarterly boundary duplicates daily values (bug), banker's rounding, unused customers, 10 rules |
| weekend_transaction_pattern | analyst-7 | PASS | 1 | Approved -- over-sourced AP10, Sunday weekly summary (W3a), decimal arithmetic, first-week edge case |
| regulatory_exposure_summary | analyst-3 | PASS | 2 | Approved -- revision corrects MidpointRounding claim, decimal arithmetic with implicit banker's rounding, exposure formula, cross-date inflation |
| investment_risk_profile | analyst-2 | PASS | 1 | Approved -- risk tier thresholds, asymmetric NULL handling, unused customers, naming mismatch observation |
| portfolio_value_summary | analyst-2 | PASS | 1 | Approved -- weekend fallback, per-customer aggregation, unused investments, customer name enrichment |
| portfolio_concentration | analyst-2 | PASS | 1 | Approved -- W4 integer division + W6 double arithmetic dual bugs, sector lookup, division-by-zero risk |
| securities_directory | analyst-2 | PASS | 1 | Approved -- clean SQL-only pass-through, unused holdings, weekend data observation |
| top_holdings_by_value | analyst-2 | PASS | 1 | Approved -- CTE analysis, unused CTE, ROW_NUMBER non-determinism, tier classification, 50 numParts for 20 rows |
| preference_change_count | analyst-5 | PASS | 1 | Approved -- dead RANK computation, unused customers, misleading name, clean SQL analysis |
| preference_summary | analyst-5 | PASS | 1 | Approved -- per-type aggregation, unused customers, as_of from first row, trailer format |
| preference_trend | analyst-5 | PASS | 2 | Approved -- revision corrects header-in-Append behavior, clean SQL trend analysis with Append mode |
| sms_opt_in_rate | analyst-5 | PASS | 1 | Approved -- integer division bug (W4), structural twin of EmailOptInRate, all 3 sources used |
| suspicious_wire_flags | analyst-3 | PASS | 1 | Approved -- OFFSHORE case-sensitive, HIGH_AMOUNT >50000, mutually exclusive flags, empty output with current data (also reviewed by reviewer-2 from analyst-9) |
| transaction_anomaly_flags | analyst-3 | PASS | 1 | Approved -- 3-sigma detection, mixed decimal/double precision, banker's rounding, dead-end customers, 13 rules (also reviewed by reviewer-2 from analyst-7) |
