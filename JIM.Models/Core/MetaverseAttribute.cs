﻿using JIM.Models.Search;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Core;

[Index(nameof(Name))]
public class MetaverseAttribute
{
    public int Id { get; set; }

    public DateTime Created { set; get; } = DateTime.UtcNow;

    public string Name { get; set; } = null!;

    public AttributeDataType Type { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    public bool BuiltIn { get; set; }

    public List<MetaverseObjectType> MetaverseObjectTypes { get; set; } = null!;

    public List<PredefinedSearchAttribute> PredefinedSearchAttributes { get; set; } = null!;
}