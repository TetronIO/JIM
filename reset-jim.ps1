docker kill $(docker ps -q)
docker system prune -f
docker volume rm jim-db-volume
docker volume rm jim-logs-volume

# delete all bin and obj folders
#gci -include bin,obj -recurse | remove-item -force -recurse