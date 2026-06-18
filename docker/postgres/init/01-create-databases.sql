SELECT 'CREATE DATABASE swarmroute_map'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'swarmroute_map')\gexec

SELECT 'CREATE DATABASE swarmroute_traffic'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'swarmroute_traffic')\gexec
