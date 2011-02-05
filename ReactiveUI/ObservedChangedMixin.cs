﻿using System;
using System.Collections.Generic;
using System.Disposables;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ReactiveUI
{
    public static class ObservedChangedMixin
    {
        static MemoizingMRUCache<string, string[]> propStringToNameCache = 
            new MemoizingMRUCache<string, string[]>((x,_) => x.Split('.'), 25);

        /// <summary>
        /// Returns the current value of a property given a notification that it has changed.
        /// </summary>
        /// <returns>The current value of the property</returns>
        public static TValue GetValue<TSender, TValue>(this IObservedChange<TSender, TValue> This)
        {
            TValue ret;
            if (!This.TryGetValue(out ret)) {
                throw new Exception(String.Format("One of the properties in the expression '{0}' was null", This.PropertyName));
            }
            return ret;
        }

        /// <summary>
        /// Attempts to return the current value of a property given a 
        /// notification that it has changed. If any property in the
        /// property expression is null, false is returned.
        /// </summary>
        /// <param name="changeValue">The value of the property expression.</param>
        /// <returns>True if the entire expression was able to be followed, false otherwise</returns>
        public static bool TryGetValue<TSender, TValue>(this IObservedChange<TSender, TValue> This, out TValue changeValue)
        {
            if (!Equals(This.Value, default(TValue))) {
                changeValue = This.Value;
                return true;
            }

            object current = This.Sender;
            string[] propNames = null;;
            lock(propStringToNameCache) { propNames = propStringToNameCache.Get(This.PropertyName); }

            PropertyInfo pi;
            foreach(var propName in propNames.SkipLast(1)) {
                if (current == null) {
                    changeValue = default(TValue);
                    return false;
                }

                pi = RxApp.getPropertyInfoOrThrow(current.GetType(), propName);
                current = pi.GetValue(current, null);
            }

            if (current == null) {
                changeValue = default(TValue);
                return false;
            }

            pi = RxApp.getPropertyInfoOrThrow(current.GetType(), propNames.Last());
            changeValue = (TValue)pi.GetValue(current, null);
            return true;
        }

        internal static IObservedChange<TSender, TValue> fillInValue<TSender, TValue>(this IObservedChange<TSender, TValue> This)
        {
            // XXX: This is an internal method because I'm unsafely upcasting,
            // but in certain cases it's needed.
            var ret = (ObservedChange<TSender, TValue>)This;
            var val = default(TValue);
            This.TryGetValue(out val);
            ret.Value = val;
            return ret;
        }

        public static void SetValueToProperty<TSender, TValue, TTarget>(
            this IObservedChange<TSender, TValue> This, 
            TTarget target,
            Expression<Func<TTarget, TValue>> property)
        {
            object current = target;
            string[] propNames = RxApp.expressionToPropertyNames(property);

            PropertyInfo pi;
            foreach(var propName in propNames.SkipLast(1)) {
                pi = RxApp.getPropertyInfoOrThrow(current.GetType(), propName);
                current = pi.GetValue(current, null);
            }

            pi = RxApp.getPropertyInfoForProperty(current.GetType(), propNames.Last());
            pi.SetValue(current, This.GetValue(), null);
        }

        /// <summary>
        /// Given a stream of notification changes, this method will convert 
        /// the property changes to the current value of the property.
        /// </summary>
        public static IObservable<TValue> Value<TSender, TValue>(
		    this IObservable<IObservedChange<TSender, TValue>> This)
        {
            return This.Select(GetValue);
        }

        public static IObservable<TValue> ValueIfNotDefault<TSender, TValue>(
		    this IObservable<IObservedChange<TSender, TValue>> This)
        {
            return This.Value().Where(x => EqualityComparer<TValue>.Default.Equals(x, default(TValue)) == false);
        }

        /// <summary>
        /// Given a stream of notification changes, this method will convert 
        /// the property changes to the current value of the property.
        /// </summary>
        public static IObservable<TRet> Value<TSender, TValue, TRet>(
                this IObservable<IObservedChange<TSender, TValue>> This)
        {
            // XXX: There is almost certainly a non-retarded way to do this
            return This.Select(x => (TRet)((object)GetValue(x)));
        }
    }

    public static class BindingMixins
    {
        public static IDisposable BindTo<TTarget, TValue>(
                this IObservable<TValue> This, 
                TTarget target,
                Expression<Func<TTarget, TValue>> property)
            where TTarget : IReactiveNotifyPropertyChanged
        {
            var sourceSub = new MutableDisposable();
            var source = This.Publish(new Subject<TValue>());

            var subscribify = new Action<TTarget, string[]>((tgt, propNames) => {
                if (sourceSub.Disposable != null) {
                    sourceSub.Disposable.Dispose();
                }

                object current = tgt;
                PropertyInfo pi = null;
                foreach(var propName in propNames.SkipLast(1)) {
                    if (current == null) {
                        return;
                    }

                    pi = RxApp.getPropertyInfoOrThrow(current.GetType(), propName);
                    current = pi.GetValue(current, null);
                }
                if (current == null) {
                    return;
                }

                pi = RxApp.getPropertyInfoOrThrow(current.GetType(), propNames.Last());
                sourceSub.Disposable = This.Subscribe(x => {
                    pi.SetValue(current, x, null);
                });
            });

            IDisposable[] toDispose = new IDisposable[] {sourceSub, null};
            string[] propertyNames = RxApp.expressionToPropertyNames(property);
            toDispose[1] = target.ObservableForProperty(property).Subscribe(_ => subscribify(target, propertyNames));

            subscribify(target, propertyNames);

            return Disposable.Create(() => { toDispose[0].Dispose(); toDispose[1].Dispose(); });
        }
    }
}

// vim: tw=120 ts=4 sw=4 et :
