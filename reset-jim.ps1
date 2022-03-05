docker kill $(docker ps -q)
docker system prune -f
docker volume rm jim-db-volume
docker volume rm jim-logs-volume