with params as (
	select '2024-10-07'::date as etl_effective_date
), max_customers_date as (
	select ifw_effective_date
	from datalake.customers cross join params
	where ifw_effective_date <= params.etl_effective_date
	order by ifw_effective_date desc
	limit 1
),scores as (
	select 
		cs.customer_id,
		cs.bureau,
		cs.ifw_effective_date,
		cs.score,
		row_number() over (partition by cs.customer_id, cs.bureau order by cs.ifw_effective_date desc) as ordinal
	from datalake.credit_scores cs cross join params p
	where cs.ifw_effective_date <= p.etl_effective_date
)
select 
	s1.customer_id,
	c.sort_name,
	s1.bureau,
	s1.ifw_effective_date,
	s1.score as current_score,
	s2.ifw_effective_date as past_ifw_effective_date,
	s2.score as past_score
from scores as s1 left join scores as s2 on 
	s1.customer_id = s2.customer_id
	and s1.bureau = s2.bureau
	and s1.ordinal = s2.ordinal -1
cross join max_customers_date as mcd
left join datalake.customers c on 
	c.ifw_effective_date = mcd.ifw_effective_date
	and s1.customer_id = c.id
where s1.ordinal = 1
and s1.score <> s2.score
order by s1.customer_id, s1.bureau