﻿using System;

namespace Ramp.Aspects
{
    [AttributeUsage(AttributeTargets.Property)]
    public class PropertyInterceptionAspect : Aspect, IPropertyInterceptionAspect
    {
        public virtual void OnGetValue(PropertyInterceptionArgs args)
        {
        }

        public virtual void OnSetValue(PropertyInterceptionArgs args)
        {
        }
    }
}