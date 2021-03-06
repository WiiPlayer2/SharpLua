/*
 ** $Id: lapi.c,v 2.55.1.5 2008/07/04 18:41:18 roberto Exp $
 ** Lua API
 ** See Copyright Notice in lua.h
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace SharpLua
{
    using lu_mem = System.UInt32;
    using TValue = Lua.lua_TValue;
    using StkId = Lua.lua_TValue;
    using lua_Integer = System.Int32;
    using lua_Number = System.Double;
    using ptrdiff_t = System.Int32;
    using ZIO = Lua.Zio;

    public partial class Lua
    {
        public const string lua_ident =
            "$Lua: " + LUA_RELEASE + " " + LUA_COPYRIGHT + " $\n" +
            "$Authors: " + LUA_AUTHORS + " $\n" +
            "$URL: www.lua.org $\n";

        public static void api_checknelems(LuaState L, int n)
        {
            api_check(L, n <= L.top - L.base_);
        }

        public static void api_checkvalidindex(LuaState L, StkId i)
        {
            api_check(L, i != luaO_nilobject);
        }

        public static void api_incr_top(LuaState L)
        {
            api_check(L, L.top < L.ci.top);
            StkId.inc(ref L.top);
        }

        public static TValue index2adr(LuaState L, int idx)
        {
            if (idx > 0)
            {
                TValue o = L.base_ + (idx - 1);
                api_check(L, idx <= L.ci.top - L.base_);
                if (o >= L.top) return luaO_nilobject;
                else return o;
            }
            else if (idx > LUA_REGISTRYINDEX)
            {
                api_check(L, idx != 0 && -idx <= L.top - L.base_);
                return L.top + idx;
            }
            else
                switch (idx)
                {  /* pseudo-indices */
                    case LUA_REGISTRYINDEX: return registry(L);
                    case LUA_ENVIRONINDEX:
                        {
                            Closure func = curr_func(L);
                            sethvalue(L, L.env, func.c.env);
                            return L.env;
                        }
                    case LUA_GLOBALSINDEX: return gt(L);
                    default:
                        {
                            Closure func = curr_func(L);
                            idx = LUA_GLOBALSINDEX - idx;
                            return (idx <= func.c.nupvalues)
                                ? func.c.upvalue[idx - 1]
                                : (TValue)luaO_nilobject;
                        }
                }
        }


        internal static Table getcurrenv(LuaState L)
        {
            if (L.ci == L.base_ci[0])  /* no enclosing function? */
                return hvalue(gt(L));  /* use global table as environment */
            else
            {
                Closure func = curr_func(L);
                return func.c.env;
            }
        }


        public static void luaA_pushobject(LuaState L, TValue o)
        {
            setobj2s(L, L.top, o);
            api_incr_top(L);
        }


        public static int lua_checkstack(LuaState L, int size)
        {
            int res = 1;
            lua_lock(L);
            if (size > LUAI_MAXCSTACK || (L.top - L.base_ + size) > LUAI_MAXCSTACK)
                res = 0;  /* stack overflow */
            else if (size > 0)
            {
                luaD_checkstack(L, size);
                if (L.ci.top < L.top + size)
                    L.ci.top = L.top + size;
            }
            lua_unlock(L);
            return res;
        }


        public static void lua_xmove(LuaState from, LuaState to, int n)
        {
            int i;
            if (from == to) return;
            lua_lock(to);
            api_checknelems(from, n);
            api_check(from, G(from) == G(to));
            api_check(from, to.ci.top - to.top >= n);
            from.top -= n;
            for (i = 0; i < n; i++)
            {
                setobj2s(to, StkId.inc(ref to.top), from.top + i);
            }
            lua_unlock(to);
        }


        public static void lua_setlevel(LuaState from, LuaState to)
        {
            to.nCcalls = from.nCcalls;
        }


        public static lua_CFunction lua_atpanic(LuaState L, lua_CFunction panicf)
        {
            lua_CFunction old;
            lua_lock(L);
            old = G(L).panic;
            G(L).panic = panicf;
            lua_unlock(L);
            return old;
        }


        public static LuaState lua_newthread(LuaState L)
        {
            LuaState L1;
            lua_lock(L);
            luaC_checkGC(L);
            L1 = luaE_newthread(L);
            setthvalue(L, L.top, L1);
            api_incr_top(L);
            lua_unlock(L);
            luai_userstatethread(L, L1);
            return L1;
        }



        /*
         ** basic stack manipulation
         */


        public static int lua_gettop(LuaState L)
        {
            return cast_int(L.top - L.base_);
        }


        public static void lua_settop(LuaState L, int idx)
        {
            lua_lock(L);
            if (idx >= 0)
            {
                api_check(L, idx <= L.stack_last - L.base_);
                while (L.top < L.base_ + idx)
                    setnilvalue(StkId.inc(ref L.top));
                L.top = L.base_ + idx;
            }
            else
            {
                api_check(L, -(idx + 1) <= (L.top - L.base_));
                L.top += idx + 1;  /* `subtract' index (index is negative) */
            }
            lua_unlock(L);
        }


        public static void lua_remove(LuaState L, int idx)
        {
            StkId p;
            lua_lock(L);
            p = index2adr(L, idx);
            api_checkvalidindex(L, p);
            while ((p = p[1]) < L.top) setobjs2s(L, p - 1, p);
            StkId.dec(ref L.top);
            lua_unlock(L);
        }


        public static void lua_insert(LuaState L, int idx)
        {
            StkId p;
            StkId q;
            lua_lock(L);
            p = index2adr(L, idx);
            api_checkvalidindex(L, p);
            for (q = L.top; q > p; StkId.dec(ref q)) setobjs2s(L, q, q - 1);
            setobjs2s(L, p, L.top);
            lua_unlock(L);
        }


        public static void lua_replace(LuaState L, int idx)
        {
            StkId o;
            lua_lock(L);
            /* explicit test for incompatible code */
            if (idx == LUA_ENVIRONINDEX && L.ci == L.base_ci[0])
                luaG_runerror(L, "no calling environment");
            api_checknelems(L, 1);
            o = index2adr(L, idx);
            api_checkvalidindex(L, o);
            if (idx == LUA_ENVIRONINDEX)
            {
                Closure func = curr_func(L);
                api_check(L, ttistable(L.top - 1));
                func.c.env = hvalue(L.top - 1);
                luaC_barrier(L, func, L.top - 1);
            }
            else
            {
                setobj(L, o, L.top - 1);
                if (idx < LUA_GLOBALSINDEX)  /* function upvalue? */
                    luaC_barrier(L, curr_func(L), L.top - 1);
            }
            StkId.dec(ref L.top);
            lua_unlock(L);
        }


        public static void lua_pushvalue(LuaState L, int idx)
        {
            lua_lock(L);
            setobj2s(L, L.top, index2adr(L, idx));
            api_incr_top(L);
            lua_unlock(L);
        }



        /*
         ** access functions (stack . C)
         */


        public static int lua_type(LuaState L, int idx)
        {
            StkId o = index2adr(L, idx);
            return (o == luaO_nilobject) ? LUA_TNONE : ttype(o);
        }


        public static CharPtr lua_typename(LuaState L, int t)
        {
            //UNUSED(L);
            return (t == LUA_TNONE) ? "no value" : luaT_typenames[t].ToString();
        }


        public static bool lua_iscfunction(LuaState L, int idx)
        {
            StkId o = index2adr(L, idx);
            return iscfunction(o);
        }


        public static int lua_isnumber(LuaState L, int idx)
        {
            TValue n = new TValue();
            TValue o = index2adr(L, idx);
            return tonumber(ref o, n);
        }


        public static int lua_isstring(LuaState L, int idx)
        {
            int t = lua_type(L, idx);
            return (t == LUA_TSTRING || t == LUA_TNUMBER) ? 1 : 0;
        }


        public static int lua_isuserdata(LuaState L, int idx)
        {
            TValue o = index2adr(L, idx);
            return (ttisuserdata(o) || ttislightuserdata(o)) ? 1 : 0;
        }


        public static int lua_rawequal(LuaState L, int index1, int index2)
        {
            StkId o1 = index2adr(L, index1);
            StkId o2 = index2adr(L, index2);
            return (o1 == luaO_nilobject || o2 == luaO_nilobject) ? 0
                : luaO_rawequalObj(o1, o2);
        }


        public static int lua_equal(LuaState L, int index1, int index2)
        {
            StkId o1, o2;
            int i;
            lua_lock(L);  /* may call tag method */
            o1 = index2adr(L, index1);
            o2 = index2adr(L, index2);
            i = (o1 == luaO_nilobject || o2 == luaO_nilobject) ? 0 : equalobj(L, o1, o2);
            lua_unlock(L);
            return i;
        }


        public static int lua_lessthan(LuaState L, int index1, int index2)
        {
            StkId o1, o2;
            int i;
            lua_lock(L);  /* may call tag method */
            o1 = index2adr(L, index1);
            o2 = index2adr(L, index2);
            i = (o1 == luaO_nilobject || o2 == luaO_nilobject) ? 0
                : luaV_lessthan(L, o1, o2);
            lua_unlock(L);
            return i;
        }



        public static lua_Number lua_tonumber(LuaState L, int idx)
        {
            TValue n = new TValue();
            TValue o = index2adr(L, idx);
            if (tonumber(ref o, n) != 0)
                return nvalue(o);
            else
                return 0;
        }


        public static lua_Integer lua_tointeger(LuaState L, int idx)
        {
            TValue n = new TValue();
            TValue o = index2adr(L, idx);
            if (tonumber(ref o, n) != 0)
            {
                lua_Integer res;
                lua_Number num = nvalue(o);
                lua_number2integer(out res, num);
                return res;
            }
            else
                return 0;
        }


        public static int lua_toboolean(LuaState L, int idx)
        {
            TValue o = index2adr(L, idx);
            return (l_isfalse(o) == 0) ? 1 : 0;
        }

        public static CharPtr lua_tolstring(LuaState L, int idx, out uint len)
        {
            StkId o = index2adr(L, idx);
            if (!ttisstring(o))
            {
                lua_lock(L);  /* `luaV_tostring' may create a new string */
                if (luaV_tostring(L, o) == 0)
                {  /* conversion failed? */
                    len = 0;
                    lua_unlock(L);
                    return null;
                }
                luaC_checkGC(L);
                o = index2adr(L, idx);  /* previous call may reallocate the stack */
                lua_unlock(L);
            }
            len = tsvalue(o).len;
            return svalue(o);
        }


        public static uint lua_objlen(LuaState L, int idx)
        {
            StkId o = index2adr(L, idx);
            switch (ttype(o))
            {
                case LUA_TSTRING: return tsvalue(o).len;
                case LUA_TUSERDATA: return uvalue(o).len;
                case LUA_TTABLE:
                    // Table now respects __len metamethod
                    Table h = hvalue(o);
                    TValue tm = fasttm(L, h.metatable, TMS.TM_LEN);
                    if (tm != null)
                        //return call_binTM(L, o, luaO_nilobject, ra, TMS.TM_LEN);
                        throw new NotImplementedException();
                    else
                        return (uint)luaH_getn(hvalue(o));
                case LUA_TNUMBER:
                    {
                        uint l;
                        lua_lock(L);  /* 'luaV_tostring' may create a new string */
                        l = (luaV_tostring(L, o) != 0 ? tsvalue(o).len : 0);
                        lua_unlock(L);
                        return l;
                    }
                default: return 0;
            }
        }


        public static lua_CFunction lua_tocfunction(LuaState L, int idx)
        {
            StkId o = index2adr(L, idx);
            return (!iscfunction(o)) ? null : clvalue(o).c.f;
        }


        public static object lua_touserdata(LuaState L, int idx)
        {
            StkId o = index2adr(L, idx);
            switch (ttype(o))
            {
                case LUA_TUSERDATA: return (rawuvalue(o).user_data);
                case LUA_TLIGHTUSERDATA: return pvalue(o);
                default: return null;
            }
        }


        public static LuaState lua_tothread(LuaState L, int idx)
        {
            StkId o = index2adr(L, idx);
            return (!ttisthread(o)) ? null : thvalue(o);
        }


        public static object lua_topointer(LuaState L, int idx)
        {
            StkId o = index2adr(L, idx);
            switch (ttype(o))
            {
                case LUA_TTABLE: return hvalue(o);
                case LUA_TFUNCTION: return clvalue(o);
                case LUA_TTHREAD: return thvalue(o);
                case LUA_TUSERDATA:
                case LUA_TLIGHTUSERDATA:
                    return lua_touserdata(L, idx);
                default: return null;
            }
        }



        /*
         ** push functions (C . stack)
         */


        public static void lua_pushnil(LuaState L)
        {
            lua_lock(L);
            setnilvalue(L.top);
            api_incr_top(L);
            lua_unlock(L);
        }


        public static void lua_pushnumber(LuaState L, lua_Number n)
        {
            lua_lock(L);
            setnvalue(L.top, n);
            api_incr_top(L);
            lua_unlock(L);
        }


        public static void lua_pushinteger(LuaState L, lua_Integer n)
        {
            lua_lock(L);
            setnvalue(L.top, cast_num(n));
            api_incr_top(L);
            lua_unlock(L);
        }


        public static void lua_pushlstring(LuaState L, CharPtr s, uint len)
        {
            lua_lock(L);
            luaC_checkGC(L);
            setsvalue2s(L, L.top, luaS_newlstr(L, s, len));
            api_incr_top(L);
            lua_unlock(L);
        }


        public static void lua_pushstring(LuaState L, CharPtr s)
        {
            if (s == null)
                lua_pushnil(L);
            else
                lua_pushlstring(L, s, (uint)strlen(s));
        }


        public static CharPtr lua_pushvfstring(LuaState L, CharPtr fmt,
                                               object[] argp)
        {
            CharPtr ret;
            lua_lock(L);
            luaC_checkGC(L);
            ret = luaO_pushvfstring(L, fmt, argp);
            lua_unlock(L);
            return ret;
        }


        public static CharPtr lua_pushfstring(LuaState L, CharPtr fmt)
        {
            CharPtr ret;
            lua_lock(L);
            luaC_checkGC(L);
            ret = luaO_pushvfstring(L, fmt, null);
            lua_unlock(L);
            return ret;
        }

        public static CharPtr lua_pushfstring(LuaState L, CharPtr fmt, params object[] p)
        {
            CharPtr ret;
            lua_lock(L);
            luaC_checkGC(L);
            ret = luaO_pushvfstring(L, fmt, p);
            lua_unlock(L);
            return ret;
        }

        public static void lua_pushcclosure(LuaState L, lua_CFunction fn, int n)
        {
            Closure cl;
            lua_lock(L);
            luaC_checkGC(L);
            api_checknelems(L, n);
            cl = luaF_newCclosure(L, n, getcurrenv(L));
            cl.c.f = fn;
            L.top -= n;
            while (n-- != 0)
                setobj2n(L, cl.c.upvalue[n], L.top + n);
            setclvalue(L, L.top, cl);
            lua_assert(iswhite(obj2gco(cl)));
            api_incr_top(L);
            lua_unlock(L);
        }


        public static void lua_pushboolean(LuaState L, int b)
        {
            lua_lock(L);
            setbvalue(L.top, (b != 0) ? 1 : 0);  /* ensure that true is 1 */
            api_incr_top(L);
            lua_unlock(L);
        }


        public static void lua_pushlightuserdata(LuaState L, object p)
        {
            lua_lock(L);
            setpvalue(L.top, p);
            api_incr_top(L);
            lua_unlock(L);
        }


        public static int lua_pushthread(LuaState L)
        {
            lua_lock(L);
            setthvalue(L, L.top, L);
            api_incr_top(L);
            lua_unlock(L);
            return (G(L).mainthread == L) ? 1 : 0;
        }



        /*
         ** get functions (Lua . stack)
         */


        public static void lua_gettable(LuaState L, int idx)
        {
            StkId t;
            lua_lock(L);
            t = index2adr(L, idx);
            api_checkvalidindex(L, t);
            luaV_gettable(L, t, L.top - 1, L.top - 1);
            lua_unlock(L);
        }

        public static void lua_getfield(LuaState L, int idx, CharPtr k)
        {
            StkId t;
            TValue key = new TValue();
            lua_lock(L);
            t = index2adr(L, idx);
            api_checkvalidindex(L, t);
            setsvalue(L, key, luaS_new(L, k));
            luaV_gettable(L, t, key, L.top);
            api_incr_top(L);
            lua_unlock(L);
        }


        public static void lua_rawget(LuaState L, int idx)
        {
            StkId t;
            lua_lock(L);
            t = index2adr(L, idx);
            api_check(L, ttistable(t));
            setobj2s(L, L.top - 1, luaH_get(hvalue(t), L.top - 1));
            lua_unlock(L);
        }


        public static void lua_rawgeti(LuaState L, int idx, int n)
        {
            StkId o;
            lua_lock(L);
            o = index2adr(L, idx);
            api_check(L, ttistable(o));
            setobj2s(L, L.top, luaH_getnum(hvalue(o), n));
            api_incr_top(L);
            lua_unlock(L);
        }


        public static void lua_createtable(LuaState L, int narray, int nrec)
        {
            lua_lock(L);
            luaC_checkGC(L);
            sethvalue(L, L.top, luaH_new(L, narray, nrec));
            api_incr_top(L);
            lua_unlock(L);
        }


        public static int lua_getmetatable(LuaState L, int objindex)
        {
            TValue obj;
            Table mt = null;
            int res;
            lua_lock(L);
            obj = index2adr(L, objindex);
            switch (ttype(obj))
            {
                case LUA_TTABLE:
                    mt = hvalue(obj).metatable;
                    break;
                case LUA_TUSERDATA:
                    mt = uvalue(obj).metatable;
                    break;
                default:
                    mt = G(L).mt[ttype(obj)];
                    break;
            }
            if (mt == null)
                res = 0;
            else
            {
                sethvalue(L, L.top, mt);
                api_incr_top(L);
                res = 1;
            }
            lua_unlock(L);
            return res;
        }


        public static void lua_getfenv(LuaState L, int idx)
        {
            StkId o;
            lua_lock(L);
            o = index2adr(L, idx);
            api_checkvalidindex(L, o);
            switch (ttype(o))
            {
                case LUA_TFUNCTION:
                    sethvalue(L, L.top, clvalue(o).c.env);
                    break;
                case LUA_TUSERDATA:
                    sethvalue(L, L.top, uvalue(o).env);
                    break;
                case LUA_TTHREAD:
                    setobj2s(L, L.top, gt(thvalue(o)));
                    break;
                default:
                    setnilvalue(L.top);
                    break;
            }
            api_incr_top(L);
            lua_unlock(L);
        }


        /*
         ** set functions (stack . Lua)
         */


        public static void lua_settable(LuaState L, int idx)
        {
            StkId t;
            lua_lock(L);
            api_checknelems(L, 2);
            t = index2adr(L, idx);
            api_checkvalidindex(L, t);
            luaV_settable(L, t, L.top - 2, L.top - 1);
            L.top -= 2;  /* pop index and value */
            lua_unlock(L);
        }


        public static void lua_setfield(LuaState L, int idx, CharPtr k)
        {
            StkId t;
            TValue key = new TValue();
            lua_lock(L);
            api_checknelems(L, 1);
            t = index2adr(L, idx);
            api_checkvalidindex(L, t);
            setsvalue(L, key, luaS_new(L, k));
            luaV_settable(L, t, key, L.top - 1);
            StkId.dec(ref L.top);  /* pop value */
            lua_unlock(L);
        }


        public static void lua_rawset(LuaState L, int idx)
        {
            StkId t;
            lua_lock(L);
            api_checknelems(L, 2);
            t = index2adr(L, idx);
            api_check(L, ttistable(t));
            setobj2t(L, luaH_set(L, hvalue(t), L.top - 2), L.top - 1);
            luaC_barriert(L, hvalue(t), L.top - 1);
            L.top -= 2;
            lua_unlock(L);
        }


        public static void lua_rawseti(LuaState L, int idx, int n)
        {
            StkId o;
            lua_lock(L);
            api_checknelems(L, 1);
            o = index2adr(L, idx);
            api_check(L, ttistable(o));
            setobj2t(L, luaH_setnum(L, hvalue(o), n), L.top - 1);
            luaC_barriert(L, hvalue(o), L.top - 1);
            StkId.dec(ref L.top);
            lua_unlock(L);
        }


        public static int lua_setmetatable(LuaState L, int objindex)
        {
            TValue obj;
            Table mt;
            lua_lock(L);
            api_checknelems(L, 1);
            obj = index2adr(L, objindex);
            api_checkvalidindex(L, obj);
            if (ttisnil(L.top - 1))
                mt = null;
            else
            {
                api_check(L, ttistable(L.top - 1));
                mt = hvalue(L.top - 1);
            }
            switch (ttype(obj))
            {
                case LUA_TTABLE:
                    {
                        hvalue(obj).metatable = mt;
                        if (mt != null)
                            luaC_objbarriert(L, hvalue(obj), mt);
                        break;
                    }
                case LUA_TUSERDATA:
                    {
                        uvalue(obj).metatable = mt;
                        if (mt != null)
                            luaC_objbarrier(L, rawuvalue(obj), mt);
                        break;
                    }
                default:
                    {
                        G(L).mt[ttype(obj)] = mt;
                        break;
                    }
            }
            StkId.dec(ref L.top);
            lua_unlock(L);
            return 1;
        }


        public static int lua_setfenv(LuaState L, int idx)
        {
            StkId o;
            int res = 1;
            lua_lock(L);
            api_checknelems(L, 1);
            o = index2adr(L, idx);
            api_checkvalidindex(L, o);
            api_check(L, ttistable(L.top - 1));
            switch (ttype(o))
            {
                case LUA_TFUNCTION:
                    clvalue(o).c.env = hvalue(L.top - 1);
                    break;
                case LUA_TUSERDATA:
                    uvalue(o).env = hvalue(L.top - 1);
                    break;
                case LUA_TTHREAD:
                    sethvalue(L, gt(thvalue(o)), hvalue(L.top - 1));
                    break;
                default:
                    res = 0;
                    break;
            }
            if (res != 0) luaC_objbarrier(L, gcvalue(o), hvalue(L.top - 1));
            StkId.dec(ref L.top);
            lua_unlock(L);
            return res;
        }


        /*
         ** `load' and `call' functions (run Lua code)
         */


        public static void adjustresults(LuaState L, int nres)
        {
            if (nres == LUA_MULTRET && L.top >= L.ci.top)
                L.ci.top = L.top;
        }


        public static void checkresults(LuaState L, int na, int nr)
        {
            api_check(L, (nr) == LUA_MULTRET || (L.ci.top - L.top >= (nr) - (na)));
        }


        public static void lua_call(LuaState L, int nargs, int nresults)
        {
            StkId func;
            lua_lock(L);
            api_checknelems(L, nargs + 1);
            checkresults(L, nargs, nresults);
            func = L.top - (nargs + 1);
            luaD_call(L, func, nresults);
            adjustresults(L, nresults);
            lua_unlock(L);
        }



        /*
         ** Execute a protected call.
         */
        public class CallS
        {  /* data to `f_call' */
            public StkId func;
            public int nresults;
        };


        static void f_call(LuaState L, object ud)
        {
            CallS c = ud as CallS;
            luaD_call(L, c.func, c.nresults);
        }



        public static int lua_pcall(LuaState L, int nargs, int nresults, int errfunc)
        {
            CallS c = new CallS();
            int status;
            ptrdiff_t func;
            lua_lock(L);
            api_checknelems(L, nargs + 1);
            checkresults(L, nargs, nresults);
            if (errfunc == 0)
                func = 0;
            else
            {
                StkId o = index2adr(L, errfunc);
                api_checkvalidindex(L, o);
                func = savestack(L, o);
            }
            c.func = L.top - (nargs + 1);  /* function to be called */
            c.nresults = nresults;
            status = luaD_pcall(L, f_call, c, savestack(L, c.func), func);
            adjustresults(L, nresults);
            lua_unlock(L);
            return status;
        }


        /*
         ** Execute a protected C call.
         */
        public class CCallS
        {  /* data to `f_Ccall' */
            public lua_CFunction func;
            public object ud;
        };


        static void f_Ccall(LuaState L, object ud)
        {
            CCallS c = ud as CCallS;
            Closure cl;
            cl = luaF_newCclosure(L, 0, getcurrenv(L));
            cl.c.f = c.func;
            setclvalue(L, L.top, cl);  /* push function */
            api_incr_top(L);
            setpvalue(L.top, c.ud);  /* push only argument */
            api_incr_top(L);
            luaD_call(L, L.top - 2, 0);
        }


        public static int lua_cpcall(LuaState L, lua_CFunction func, object ud)
        {
            CCallS c = new CCallS();
            int status;
            lua_lock(L);
            c.func = func;
            c.ud = ud;
            status = luaD_pcall(L, f_Ccall, c, savestack(L, L.top), 0);
            lua_unlock(L);
            return status;
        }

        /// <summary>
        /// Wraps a lua_Reader and its associated data object to provide a read-ahead ("peek") ability.
        /// </summary>
        class PeekableLuaReader
        {
            readonly lua_Reader inner;
            readonly object inner_data;
            CharPtr readahead_buffer;
            uint readahead_buffer_size;

            public PeekableLuaReader(lua_Reader inner, object inner_data)
            {
                this.inner = inner;
                this.inner_data = inner_data;
            }

            public CharPtr lua_Reader(LuaState L, object ud, out uint sz)
            {
                CharPtr ret = lua_ReaderImpl(L, ud, out sz);
                //Debug.Print("PeekableLuaReader::lua_Reader() returning sz = {0}, buffer = {1}",
                //            sz,
                //            (ret == null) ? "null" :
                //            string.Concat("'", ret.ToStringDebug(), "'")
                //            );
                return ret;
            }

            CharPtr lua_ReaderImpl(LuaState L, object ud, out uint sz)
            {
                if (readahead_buffer != null && readahead_buffer_size != 0)
                {
                    CharPtr tmp = readahead_buffer;
                    sz = readahead_buffer_size;
                    readahead_buffer = null;
                    return tmp;
                }
                return inner(L, inner_data, out sz);
            }

            static readonly CharPtr empty_buffer = new CharPtr(new char[1]);

            public int peek(LuaState L, object ud)
            {
                if (readahead_buffer == null)
                {
                    readahead_buffer = inner(L, inner_data, out readahead_buffer_size);
                    if (readahead_buffer == null)
                    {
                        readahead_buffer = empty_buffer;
                        readahead_buffer_size = 0;
                    }
                }
                if (readahead_buffer_size == 0) return -1; // EOF
                return readahead_buffer[0];
            }
        }

        class CharPtrLuaReader
        {
            CharPtr buffer;
            uint size;

            public CharPtrLuaReader(CharPtr buffer, int size)
            {
                if (size < 0) throw new ArgumentException("size must be >= 0!");
                this.buffer = buffer;
                this.size = (uint)size;
            }

            public CharPtr lua_Reader(LuaState L, object ud, out uint sz)
            {
                CharPtr old_buffer = buffer;
                sz = size;

                buffer = null;
                size = 0;

                return old_buffer;
            }
        }

#if OVERRIDE_LOAD
        /// <summary>
        /// Performs SharpLua-specific preprocessing magic for lua_load()
        /// </summary>
        /// <param name="reader">Original lua_Reader.</param>
        /// <param name="data">Data object for the original lua_Reader</param>
        static void SharpLua_OverrideLoad(LuaState L, ref lua_Reader reader, ref object data)
        {
            // Wrap our reader in a PeekableLuaReader.
            PeekableLuaReader plr = new PeekableLuaReader(reader, data);
            reader = plr.lua_Reader;
            data = plr;

            // Look ahead to see if it's something we're interested in.
            int peek_char = plr.peek(L, plr);
            if (peek_char == LUA_SIGNATURE[0]) return; // if it's empty, or there's no binary value, there's no work to be done.

            // Okay, we are.  Read the whole thing into memory RIGHT NOW
            char[] cur_buffer = null;
            int cur_buffer_size = 0;
            CharPtr next_data;
            uint _next_data_size;
            while ( (next_data = reader(L, data, out _next_data_size)) != null ) 
            {
                int next_data_size = checked((int) _next_data_size);
                if (next_data_size == 0) continue;
                char[] new_buffer = new char[cur_buffer_size + next_data_size + 1];
                if (cur_buffer_size > 0) 
                {
                    Array.Copy(cur_buffer, 0, new_buffer, 0, cur_buffer_size);
                }
                Array.Copy(next_data.chars, next_data.index, new_buffer, cur_buffer_size, next_data_size);
                cur_buffer = new_buffer;
                cur_buffer_size = cur_buffer.Length - 1;
            }

            Lexer l = new Lexer();

            TokenReader tr = l.Lex(new CharPtr(cur_buffer));
            Parser p = new Parser(tr);
            Ast.Chunk c = p.Parse();

            Visitors.LuaCompatibleOutput lco = new Visitors.LuaCompatibleOutput();
            string s = lco.Format(c);
            CharPtr s_buf = new CharPtr(s);

            // Replace the provided lua_Reader with one that reads out of the CharPtrLuaReader.
            CharPtrLuaReader cplr = new CharPtrLuaReader(s_buf, s.Length);
            reader = cplr.lua_Reader;
            data = cplr;

        }
#endif

        public static int lua_load(LuaState L, lua_Reader reader, object data,
                                   CharPtr chunkname)
        {
            ZIO z = new ZIO();
            int status;
            lua_lock(L);
            if (chunkname == null) chunkname = "?";

#if OVERRIDE_LOAD
            //#if false
            // SharpLua_OverrideLoad(L, ref reader, ref data);
#endif
            luaZ_init(L, z, reader, data);
            status = luaD_protectedparser(L, z, chunkname);
            lua_unlock(L);
            if (data is LoadF)
            {
                LoadF f = data as LoadF;
                if (f.f != null)
                {
                    f.f.Close();
                }
            }
            return status;
        }


        public static int lua_dump(LuaState L, lua_Writer writer, object data)
        {
            int status;
            TValue o;
            lua_lock(L);
            api_checknelems(L, 1);
            o = L.top - 1;
            if (isLfunction(o))
                status = luaU_dump(L, clvalue(o).l.p, writer, data, 0);
            else
                status = 1;
            lua_unlock(L);
            return status;
        }


        public static int lua_status(LuaState L)
        {
            return L.status;
        }


        /*
         ** Garbage-collection function
         */

        public static int lua_gc(LuaState L, int what, int data)
        {
            int res = 0;
            GlobalState g;
            lua_lock(L);
            g = G(L);
            switch (what)
            {
                case LUA_GCSTOP:
                    {
                        g.GCthreshold = MAX_LUMEM;
                        break;
                    }
                case LUA_GCRESTART:
                    {
                        g.GCthreshold = g.totalbytes;
                        break;
                    }
                case LUA_GCCOLLECT:
                    {
                        luaC_fullgc(L);
                        break;
                    }
                case LUA_GCCOUNT:
                    {
                        /* GC values are expressed in Kbytes: #bytes/2^10 */
                        res = cast_int(g.totalbytes >> 10);
                        break;
                    }
                case LUA_GCCOUNTB:
                    {
                        res = cast_int(g.totalbytes & 0x3ff);
                        break;
                    }
                case LUA_GCSTEP:
                    {
                        lu_mem a = ((lu_mem)data << 10);
                        if (a <= g.totalbytes)
                            g.GCthreshold = (uint)(g.totalbytes - a);
                        else
                            g.GCthreshold = 0;
                        while (g.GCthreshold <= g.totalbytes)
                        {
                            luaC_step(L);
                            if (g.gcstate == GCSpause)
                            {  /* end of cycle? */
                                res = 1;  /* signal it */
                                break;
                            }
                        }
                        break;
                    }
                case LUA_GCSETPAUSE:
                    {
                        res = g.gcpause;
                        g.gcpause = data;
                        break;
                    }
                case LUA_GCSETSTEPMUL:
                    {
                        res = g.gcstepmul;
                        g.gcstepmul = data;
                        break;
                    }
                default:
                    res = -1;  /* invalid option */
                    break;
            }
            lua_unlock(L);
            return res;
        }



        /*
         ** miscellaneous functions
         */


        public static int lua_error(LuaState L)
        {
            lua_lock(L);
            api_checknelems(L, 1);
            luaG_errormsg(L);
            lua_unlock(L);
            return 0;  /* to avoid warnings */
        }


        public static int lua_next(LuaState L, int idx)
        {
            StkId t;
            int more;
            lua_lock(L);
            t = index2adr(L, idx);
            api_check(L, ttistable(t));
            more = luaH_next(L, hvalue(t), L.top - 1);
            if (more != 0)
            {
                api_incr_top(L);
            }
            else  /* no more elements */
                StkId.dec(ref L.top);  /* remove key */
            lua_unlock(L);
            return more;
        }


        public static void lua_concat(LuaState L, int n)
        {
            lua_lock(L);
            api_checknelems(L, n);
            if (n >= 2)
            {
                luaC_checkGC(L);
                luaV_concat(L, n, cast_int(L.top - L.base_) - 1);
                L.top -= (n - 1);
            }
            else if (n == 0)
            {  /* push empty string */
                setsvalue2s(L, L.top, luaS_newlstr(L, "", 0));
                api_incr_top(L);
            }
            /* else n == 1; nothing to do */
            lua_unlock(L);
        }


        public static lua_Alloc lua_getallocf(LuaState L, ref object ud)
        {
            lua_Alloc f;
            lua_lock(L);
            if (ud != null) ud = G(L).ud;
            f = G(L).frealloc;
            lua_unlock(L);
            return f;
        }


        public static void lua_setallocf(LuaState L, lua_Alloc f, object ud)
        {
            lua_lock(L);
            G(L).ud = ud;
            G(L).frealloc = f;
            lua_unlock(L);
        }


        public static object lua_newuserdata(LuaState L, uint size)
        {
            Udata u;
            lua_lock(L);
            luaC_checkGC(L);
            u = luaS_newudata(L, size, getcurrenv(L));
            setuvalue(L, L.top, u);
            api_incr_top(L);
            lua_unlock(L);
            return u.user_data;
        }

        // this one is used internally only
        internal static object lua_newuserdata(LuaState L, Type t)
        {
            Udata u;
            lua_lock(L);
            luaC_checkGC(L);
            u = luaS_newudata(L, t, getcurrenv(L));
            setuvalue(L, L.top, u);
            api_incr_top(L);
            lua_unlock(L);
            return u.user_data;
        }

        static CharPtr aux_upvalue(StkId fi, int n, ref TValue val)
        {
            Closure f;
            if (!ttisfunction(fi)) return null;
            f = clvalue(fi);
            if (f.c.isC != 0)
            {
                if (!(1 <= n && n <= f.c.nupvalues)) return null;
                val = f.c.upvalue[n - 1];
                return "";
            }
            else
            {
                Proto p = f.l.p;
                if (!(1 <= n && n <= p.sizeupvalues)) return null;
                val = f.l.upvals[n - 1].v;
                return getstr(p.upvalues[n - 1]);
            }
        }


        public static CharPtr lua_getupvalue(LuaState L, int funcindex, int n)
        {
            CharPtr name;
            TValue val = new TValue();
            lua_lock(L);
            name = aux_upvalue(index2adr(L, funcindex), n, ref val);
            if (name != null)
            {
                setobj2s(L, L.top, val);
                api_incr_top(L);
            }
            lua_unlock(L);
            return name;
        }


        public static CharPtr lua_setupvalue(LuaState L, int funcindex, int n)
        {
            CharPtr name;
            TValue val = new TValue();
            StkId fi;
            lua_lock(L);
            fi = index2adr(L, funcindex);
            api_checknelems(L, 1);
            name = aux_upvalue(fi, n, ref val);
            if (name != null)
            {
                StkId.dec(ref L.top);
                setobj(L, val, L.top);
                luaC_barrier(L, clvalue(fi), L.top);
            }
            lua_unlock(L);
            return name;
        }

    }
}
