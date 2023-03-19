pub mod ListExt {
    // use core::ops::Deref;
    use crate::List_::{cons, empty, reverse, List};
    use crate::Native_::{seq_to_iter, Vec};
    use crate::Seq_::ofList;

    impl<T: Clone> List<T> {
        //todo - non-consuming iter by ref
        // pub fn iter<'a>(&self) -> impl Iterator<Item = & 'a T> {
        //     let s = ofList(self.clone());
        //     seq_to_iter(&s)
        // }

        pub fn into_iter(&self) -> impl Iterator<Item = T> {
            let s = ofList(self.clone());
            seq_to_iter(&s)
        }
    }

    // on second thought not sure deref is ideal because it exposes the option api (iter, map etc) which is misleading
    // impl<T: Clone> Deref for List<T> {
    //     type Target = Option<LrcPtr<crate::List_::Node_1<T>>>;
    //     fn deref(&self) -> &Self::Target {
    //         &self.item
    //     }
    // }

    impl<T: Clone> From<&Vec<T>> for List<T> {
        fn from(vec: &Vec<T>) -> Self {
            let mut lst: List<T> = empty();
            for (i, item) in vec.iter().rev().enumerate() {
                lst = cons(item.clone(), lst);
            }
            lst
        }
    }

    impl<T: Clone> FromIterator<T> for List<T> {
        fn from_iter<U: IntoIterator<Item = T>>(iter: U) -> Self {
            let mut lst: List<T> = empty();
            for (i, item) in iter.into_iter().enumerate() {
                lst = cons(item, lst);
            }
            reverse(lst)
        }
    }

    impl<'a, T: Clone> FromIterator<&'a T> for List<T> {
        fn from_iter<U: IntoIterator<Item = &'a T>>(iter: U) -> Self {
            let mut lst: List<T> = empty();
            for (i, item) in iter.into_iter().enumerate() {
                lst = cons(item.clone(), lst);
            }
            reverse(lst)
        }
    }

    impl<T: Clone> Into<Vec<T>> for List<T> {
        fn into(self) -> Vec<T> {
            self.into_iter().collect()
        }
    }
}

pub mod SetExt {
    // use core::ops::Deref;
    use crate::Native_::{seq_to_iter, Vec};
    use crate::Set_::{add, empty, equals, toSeq, Set};

    impl<T: Clone + PartialOrd> Set<T> {
        //todo - non-consuming iter by ref
        // pub fn iter<'a>(&self) -> impl Iterator<Item = & 'a T> {
        //     let s = toSeq(self.clone());
        //     seq_to_iter(&s)
        // }

        pub fn into_iter(&self) -> impl Iterator<Item = T> {
            let s = toSeq(self.clone());
            seq_to_iter(&s)
        }
    }

    impl<T: Clone + PartialOrd> PartialEq for Set<T> {
        fn eq(&self, other: &Self) -> bool {
            equals(self.clone(), other.clone())
        }
    }

    impl<T: Clone + PartialOrd> From<Vec<T>> for Set<T> {
        fn from(vec: Vec<T>) -> Self {
            let mut set = empty();
            for v in vec.iter() {
                set = add(v.clone(), set);
            }
            set
        }
    }

    impl<T: Clone + PartialOrd> From<&Vec<T>> for Set<T> {
        fn from(vec: &Vec<T>) -> Self {
            let mut set = empty();
            for v in vec.iter() {
                set = add(v.clone(), set);
            }
            set
        }
    }

    impl<T: Clone + PartialOrd> FromIterator<T> for Set<T> {
        fn from_iter<U: IntoIterator<Item = T>>(iter: U) -> Self {
            let mut set = empty();
            for v in iter.into_iter() {
                set = add(v, set);
            }
            set
        }
    }

    impl<T: Clone + PartialOrd> Into<Vec<T>> for Set<T> {
        fn into(self) -> Vec<T> {
            self.into_iter().collect()
        }
    }
}

pub mod MapExt {
    // use core::ops::Deref;
    use crate::Map_::{add, empty, equals, iterate, toSeq, Map};
    use crate::Native_::{seq_to_iter, Vec};

    impl<K: Clone + PartialOrd, V: Clone> Map<K, V> {
        //todo - non-consuming iter by ref
        // pub fn iter<'a>(&self) -> impl Iterator<Item = (& 'a K, & 'a V)> {
        //     let s = toSeq(self.clone());
        //     seq_to_iter(&s).map(|kvp| kvp.as_ref().clone())
        // }

        pub fn into_iter(&self) -> impl Iterator<Item = (K, V)> {
            let s = toSeq(self.clone());
            seq_to_iter(&s).map(|kvp| kvp.as_ref().clone())
        }
    }

    impl<K: Clone + PartialOrd, V: Clone + PartialOrd> PartialEq for Map<K, V> {
        fn eq(&self, other: &Self) -> bool {
            equals(self.clone(), other.clone())
        }
    }

    impl<K: Clone + PartialOrd, V: Clone> From<&Vec<(K, V)>> for Map<K, V> {
        fn from(vec: &Vec<(K, V)>) -> Self {
            let mut map: Map<K, V> = empty();
            for (i, (k, v)) in vec.iter().rev().enumerate() {
                map = add(k.clone(), v.clone(), map);
            }
            map
        }
    }

    impl<K: Clone + PartialOrd, V: Clone> FromIterator<(K, V)> for Map<K, V> {
        fn from_iter<U: IntoIterator<Item = (K, V)>>(iter: U) -> Self {
            let mut map: Map<K, V> = empty();
            for (k, v) in iter.into_iter() {
                map = add(k, v.clone(), map);
            }
            map
        }
    }

    impl<K: Clone + PartialOrd, V: Clone> Into<Vec<(K, V)>> for Map<K, V> {
        fn into(self) -> Vec<(K, V)> {
            self.into_iter().collect()
        }
    }
}
