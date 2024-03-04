# delete all bin and obj folders
gci -include bin,obj -recurse | remove-item -force -recurse